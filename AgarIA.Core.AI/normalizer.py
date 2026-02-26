"""Running observation and reward normalization."""

import numpy as np


class RunningNormalizer:
    """Welford's online algorithm for running mean/variance, clipped to [-clip, clip]."""

    def __init__(self, shape: int, clip: float = 10.0):
        self.mean = np.zeros(shape, dtype=np.float64)
        self.var = np.ones(shape, dtype=np.float64)
        self.count = 1e-4
        self.clip = clip

    def update(self, batch: np.ndarray):
        batch_mean = batch.mean(axis=0)
        batch_var = batch.var(axis=0)
        batch_count = batch.shape[0]

        delta = batch_mean - self.mean
        total = self.count + batch_count
        self.mean += delta * batch_count / total
        m_a = self.var * self.count
        m_b = batch_var * batch_count
        m2 = m_a + m_b + delta**2 * self.count * batch_count / total
        self.var = m2 / total
        self.count = total

    def normalize(self, batch: np.ndarray) -> np.ndarray:
        std = np.sqrt(self.var + 1e-8)
        return np.clip((batch - self.mean) / std, -self.clip, self.clip).astype(
            np.float32
        )

    def state_dict(self) -> dict:
        return {
            "mean": self.mean.tolist(),
            "var": self.var.tolist(),
            "count": float(self.count),
        }

    def load_state_dict(self, d: dict):
        self.mean = np.array(d["mean"], dtype=np.float64)
        self.var = np.array(d["var"], dtype=np.float64)
        self.count = d["count"]


class RewardNormalizer:
    """Normalize rewards by running standard deviation."""

    def __init__(self):
        self.mean = 0.0
        self.var = 1.0
        self.count = 1e-4

    def update(self, rewards: np.ndarray):
        batch_mean = rewards.mean()
        batch_var = rewards.var()
        batch_count = rewards.shape[0]

        delta = batch_mean - self.mean
        total = self.count + batch_count
        self.mean += delta * batch_count / total
        m_a = self.var * self.count
        m_b = batch_var * batch_count
        self.var = (m_a + m_b + delta**2 * self.count * batch_count / total) / total
        self.count = total

    def normalize(self, rewards: np.ndarray) -> np.ndarray:
        std = max(np.sqrt(self.var + 1e-8), 1e-4)
        return np.clip(rewards / std, -10.0, 10.0).astype(np.float32)

    def state_dict(self) -> dict:
        return {"mean": self.mean, "var": self.var, "count": self.count}

    def load_state_dict(self, d: dict):
        self.mean = d["mean"]
        self.var = d["var"]
        self.count = d["count"]
