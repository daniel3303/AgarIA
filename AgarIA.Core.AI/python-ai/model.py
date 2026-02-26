"""Actor-Critic network for PPO."""

import torch
import torch.nn as nn
from config import OBS_SIZE, ACTION_SIZE, HIDDEN_SIZES, DEVICE


class ActorCriticNetwork(nn.Module):
    def __init__(self, obs_size: int = OBS_SIZE, hidden_sizes: list[int] = None):
        super().__init__()
        hidden_sizes = hidden_sizes or HIDDEN_SIZES

        # Shared hidden layers
        layers = []
        prev = obs_size
        for hs in hidden_sizes:
            layers.append(nn.Linear(prev, hs))
            layers.append(nn.Tanh())
            prev = hs
        self.shared = nn.Sequential(*layers)

        # Policy head: moveX mean, moveY mean, split logit
        self.policy_head = nn.Linear(prev, ACTION_SIZE)

        # Value head
        self.value_head = nn.Linear(prev, 1)

        # Learnable log-std for continuous actions
        self.log_std = nn.Parameter(torch.full((2,), -1.0))

        self._init_weights()

    def _init_weights(self):
        for m in self.shared:
            if isinstance(m, nn.Linear):
                nn.init.xavier_uniform_(m.weight)
                nn.init.zeros_(m.bias)
        nn.init.xavier_uniform_(self.policy_head.weight, gain=0.01)
        nn.init.zeros_(self.policy_head.bias)
        nn.init.xavier_uniform_(self.value_head.weight, gain=0.1)
        nn.init.zeros_(self.value_head.bias)

    def forward(self, obs: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        """Returns (policy_output [B, 3], value [B, 1])."""
        hidden = self.shared(obs)
        policy = self.policy_head(hidden)
        value = self.value_head(hidden)
        return policy, value

    def get_action(self, obs: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        """Sample actions and return (actions, log_probs, values).

        actions: [B, 3] — moveX, moveY (continuous), split (0/1)
        """
        policy, value = self.forward(obs)

        # Continuous: moveX, moveY
        move_mean = torch.tanh(policy[:, :2])
        std = torch.exp(self.log_std.clamp(-3.0, 0.5))
        move_dist = torch.distributions.Normal(move_mean, std)
        move_sample = move_dist.sample()
        move_log_prob = move_dist.log_prob(move_sample).sum(dim=-1)

        # Discrete: split
        split_logit = policy[:, 2]
        split_dist = torch.distributions.Bernoulli(logits=split_logit)
        split_sample = split_dist.sample()
        split_log_prob = split_dist.log_prob(split_sample)

        actions = torch.cat([move_sample, split_sample.unsqueeze(-1)], dim=-1)
        log_probs = move_log_prob + split_log_prob

        return actions, log_probs, value.squeeze(-1)

    def evaluate_actions(
        self, obs: torch.Tensor, actions: torch.Tensor
    ) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        """Evaluate log_prob, value, entropy for given obs and actions."""
        policy, value = self.forward(obs)

        # Continuous
        move_mean = torch.tanh(policy[:, :2])
        std = torch.exp(self.log_std.clamp(-3.0, 0.5))
        move_dist = torch.distributions.Normal(move_mean, std)
        move_log_prob = move_dist.log_prob(actions[:, :2]).sum(dim=-1)
        move_entropy = move_dist.entropy().sum(dim=-1)

        # Discrete
        split_logit = policy[:, 2]
        split_dist = torch.distributions.Bernoulli(logits=split_logit)
        split_log_prob = split_dist.log_prob(actions[:, 2])
        split_entropy = split_dist.entropy()

        log_probs = move_log_prob + split_log_prob
        entropy = move_entropy + split_entropy

        return log_probs, value.squeeze(-1), entropy
