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
    """Stores trajectory data for PPO training."""

    def __init__(self, buffer_size: int, obs_size: int):
        self.buffer_size = buffer_size
        self.obs = np.zeros((buffer_size, obs_size), dtype=np.float32)
        self.actions = np.zeros((buffer_size, 3), dtype=np.float32)
        self.log_probs = np.zeros(buffer_size, dtype=np.float32)
        self.rewards = np.zeros(buffer_size, dtype=np.float32)
        self.values = np.zeros(buffer_size, dtype=np.float32)
        self.dones = np.zeros(buffer_size, dtype=np.float32)
        self.ptr = 0

    def add(
        self,
        obs: np.ndarray,
        actions: np.ndarray,
        log_probs: np.ndarray,
        rewards: np.ndarray,
        values: np.ndarray,
        dones: np.ndarray,
    ):
        """Add a batch of transitions. Each arg is (num_bots,) or (num_bots, dim)."""
        n = obs.shape[0]
        if self.ptr + n > self.buffer_size:
            n = self.buffer_size - self.ptr

        end = self.ptr + n
        self.obs[self.ptr : end] = obs[:n]
        self.actions[self.ptr : end] = actions[:n]
        self.log_probs[self.ptr : end] = log_probs[:n]
        self.rewards[self.ptr : end] = rewards[:n]
        self.values[self.ptr : end] = values[:n]
        self.dones[self.ptr : end] = dones[:n]
        self.ptr += n

    def ready(self) -> bool:
        return self.ptr >= self.buffer_size

    def reset(self):
        self.ptr = 0


def compute_gae(
    rewards: np.ndarray,
    values: np.ndarray,
    dones: np.ndarray,
    last_value: float = 0.0,
) -> tuple[np.ndarray, np.ndarray]:
    """Compute GAE-lambda advantages and returns."""
    n = len(rewards)
    advantages = np.zeros(n, dtype=np.float32)
    last_gae = 0.0

    for t in reversed(range(n)):
        next_value = last_value if t == n - 1 else values[t + 1]
        next_non_terminal = 1.0 - dones[t]
        delta = rewards[t] + GAMMA * next_value * next_non_terminal - values[t]
        advantages[t] = last_gae = delta + GAMMA * LAMBDA * next_non_terminal * last_gae

    returns = advantages + values
    return advantages, returns


def ppo_update(
    model: ActorCriticNetwork,
    optimizer: torch.optim.Optimizer,
    buffer: RolloutBuffer,
) -> dict:
    """Run PPO update epochs on the buffer. Returns training stats."""
    advantages, returns = compute_gae(buffer.rewards, buffer.values, buffer.dones)

    # Convert to tensors
    obs_t = torch.from_numpy(buffer.obs[: buffer.ptr]).to(DEVICE)
    actions_t = torch.from_numpy(buffer.actions[: buffer.ptr]).to(DEVICE)
    old_log_probs_t = torch.from_numpy(buffer.log_probs[: buffer.ptr]).to(DEVICE)
    advantages_t = torch.from_numpy(advantages[: buffer.ptr]).to(DEVICE)
    returns_t = torch.from_numpy(returns[: buffer.ptr]).to(DEVICE)

    total_loss = 0.0
    total_policy_loss = 0.0
    total_value_loss = 0.0
    total_entropy = 0.0
    num_updates = 0

    n = buffer.ptr
    for _ in range(EPOCHS):
        indices = np.random.permutation(n)
        for start in range(0, n, MINIBATCH_SIZE):
            end = min(start + MINIBATCH_SIZE, n)
            idx = indices[start:end]
            idx_t = torch.from_numpy(idx).long().to(DEVICE)

            mb_obs = obs_t[idx_t]
            mb_actions = actions_t[idx_t]
            mb_old_log_probs = old_log_probs_t[idx_t]
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

            # Value loss
            value_loss = 0.5 * (values - mb_returns).pow(2).mean()

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
