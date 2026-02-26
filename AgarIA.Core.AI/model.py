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

        # Policy head: targetX mean, targetY mean (absolute 0-1)
        self.policy_head = nn.Linear(prev, ACTION_SIZE)

        # Value head
        self.value_head = nn.Linear(prev, 1)

        # Learnable log-std for continuous actions
        self.log_std = nn.Parameter(torch.full((ACTION_SIZE,), -1.0))

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
        """Returns (policy_output [B, 2], value [B, 1])."""
        hidden = self.shared(obs)
        policy = self.policy_head(hidden)
        value = self.value_head(hidden)
        return policy, value

    def get_action(self, obs: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        """Sample actions and return (actions, log_probs, values).

        actions: [B, 2] — targetX, targetY (absolute 0-1 via sigmoid)
        """
        policy, value = self.forward(obs)

        mean = torch.sigmoid(policy)
        std = torch.exp(self.log_std.clamp(-3.0, 0.5))
        dist = torch.distributions.Normal(mean, std)
        sample = dist.sample().clamp(0.0, 1.0)
        log_probs = dist.log_prob(sample).sum(dim=-1)

        return sample, log_probs, value.squeeze(-1)

    def evaluate_actions(
        self, obs: torch.Tensor, actions: torch.Tensor
    ) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        """Evaluate log_prob, value, entropy for given obs and actions."""
        policy, value = self.forward(obs)

        mean = torch.sigmoid(policy)
        std = torch.exp(self.log_std.clamp(-3.0, 0.5))
        dist = torch.distributions.Normal(mean, std)
        log_probs = dist.log_prob(actions).sum(dim=-1)
        entropy = dist.entropy().sum(dim=-1)

        return log_probs, value.squeeze(-1), entropy
