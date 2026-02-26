"""PPO trainer with GAE-lambda advantages and clipped surrogate loss."""

import torch
import numpy as np
from config import (
    GAMMA,
    LAMBDA,
    CLIP_EPSILON,
    ENTROPY_COEFF,
    VALUE_COEFF,
    MAX_GRAD_NORM,
    MINIBATCH_SIZE,
    EPOCHS,
    DEVICE,
)
from model import ActorCriticNetwork


class RolloutBuffer:
    """Stores trajectory data for PPO training, structured per-bot."""

    def __init__(self, num_bots: int, steps_per_bot: int, obs_size: int):
        self.num_bots = num_bots
        self.steps_per_bot = steps_per_bot
        self.obs = np.zeros((num_bots, steps_per_bot, obs_size), dtype=np.float32)
        self.actions = np.zeros((num_bots, steps_per_bot, 3), dtype=np.float32)
        self.log_probs = np.zeros((num_bots, steps_per_bot), dtype=np.float32)
        self.rewards = np.zeros((num_bots, steps_per_bot), dtype=np.float32)
        self.values = np.zeros((num_bots, steps_per_bot), dtype=np.float32)
        self.dones = np.zeros((num_bots, steps_per_bot), dtype=np.float32)
        self.step_count = 0

    def add(
        self,
        obs: np.ndarray,
        actions: np.ndarray,
        log_probs: np.ndarray,
        rewards: np.ndarray,
        values: np.ndarray,
        dones: np.ndarray,
    ):
        """Add one tick of transitions. Each arg is (num_bots,) or (num_bots, dim)."""
        if self.step_count >= self.steps_per_bot:
            return
        t = self.step_count
        self.obs[:, t] = obs
        self.actions[:, t] = actions
        self.log_probs[:, t] = log_probs
        self.rewards[:, t] = rewards
        self.values[:, t] = values
        self.dones[:, t] = dones
        self.step_count += 1

    def ready(self) -> bool:
        return self.step_count >= self.steps_per_bot

    def reset(self):
        self.step_count = 0

    def get_training_data(self) -> dict:
        """Compute GAE per bot, then flatten for minibatch sampling."""
        T = self.step_count
        all_advantages = np.zeros((self.num_bots, T), dtype=np.float32)
        all_returns = np.zeros((self.num_bots, T), dtype=np.float32)

        for b in range(self.num_bots):
            rewards = self.rewards[b, :T]
            values = self.values[b, :T]
            dones = self.dones[b, :T]

            advantages = np.zeros(T, dtype=np.float32)
            last_gae = 0.0
            for t in reversed(range(T)):
                next_value = 0.0 if t == T - 1 else values[t + 1]
                next_non_terminal = 1.0 - dones[t]
                delta = rewards[t] + GAMMA * next_value * next_non_terminal - values[t]
                advantages[t] = last_gae = delta + GAMMA * LAMBDA * next_non_terminal * last_gae

            all_advantages[b] = advantages
            all_returns[b] = advantages + values

        # Flatten (num_bots, T, ...) -> (num_bots * T, ...)
        n = self.num_bots * T
        return {
            "obs": self.obs[:, :T].reshape(n, -1),
            "actions": self.actions[:, :T].reshape(n, -1),
            "log_probs": self.log_probs[:, :T].reshape(n),
            "values": self.values[:, :T].reshape(n),
            "advantages": all_advantages.reshape(n),
            "returns": all_returns.reshape(n),
        }


def ppo_update(
    model: ActorCriticNetwork,
    optimizer: torch.optim.Optimizer,
    buffer: RolloutBuffer,
) -> dict:
    """Run PPO update epochs on the buffer. Returns training stats."""
    data = buffer.get_training_data()

    obs_t = torch.from_numpy(data["obs"]).to(DEVICE)
    actions_t = torch.from_numpy(data["actions"]).to(DEVICE)
    old_log_probs_t = torch.from_numpy(data["log_probs"]).to(DEVICE)
    old_values_t = torch.from_numpy(data["values"]).to(DEVICE)
    advantages_t = torch.from_numpy(data["advantages"]).to(DEVICE)
    returns_t = torch.from_numpy(data["returns"]).to(DEVICE)

    total_loss = 0.0
    total_policy_loss = 0.0
    total_value_loss = 0.0
    total_entropy = 0.0
    num_updates = 0

    n = obs_t.shape[0]
    for _ in range(EPOCHS):
        indices = np.random.permutation(n)
        for start in range(0, n, MINIBATCH_SIZE):
            end = min(start + MINIBATCH_SIZE, n)
            idx = indices[start:end]
            idx_t = torch.from_numpy(idx).long().to(DEVICE)

            mb_obs = obs_t[idx_t]
            mb_actions = actions_t[idx_t]
            mb_old_log_probs = old_log_probs_t[idx_t]
            mb_old_values = old_values_t[idx_t]
            mb_advantages = advantages_t[idx_t]
            mb_returns = returns_t[idx_t]

            # Normalize advantages per minibatch
            mb_advantages = (mb_advantages - mb_advantages.mean()) / (
                mb_advantages.std() + 1e-8
            )

            log_probs, values, entropy = model.evaluate_actions(mb_obs, mb_actions)

            # Clipped surrogate loss
            ratio = torch.exp(log_probs - mb_old_log_probs)
            surr1 = ratio * mb_advantages
            surr2 = torch.clamp(ratio, 1.0 - CLIP_EPSILON, 1.0 + CLIP_EPSILON) * mb_advantages
            policy_loss = -torch.min(surr1, surr2).mean()

            # Clipped value loss
            value_clipped = mb_old_values + (values - mb_old_values).clamp(-CLIP_EPSILON, CLIP_EPSILON)
            value_loss = 0.5 * torch.max(
                (values - mb_returns).pow(2),
                (value_clipped - mb_returns).pow(2),
            ).mean()

            # Entropy bonus
            entropy_loss = -entropy.mean()

            loss = policy_loss + VALUE_COEFF * value_loss + ENTROPY_COEFF * entropy_loss

            optimizer.zero_grad()
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), MAX_GRAD_NORM)
            optimizer.step()

            total_loss += loss.item()
            total_policy_loss += policy_loss.item()
            total_value_loss += value_loss.item()
            total_entropy += entropy.mean().item()
            num_updates += 1

    buffer.reset()

    return {
        "loss": total_loss / max(num_updates, 1),
        "policy_loss": total_policy_loss / max(num_updates, 1),
        "value_loss": total_value_loss / max(num_updates, 1),
        "entropy": total_entropy / max(num_updates, 1),
    }
