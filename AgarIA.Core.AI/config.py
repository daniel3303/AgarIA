"""Configuration for the Python AI sidecar."""

import logging
import os

import torch

logger = logging.getLogger(__name__)


# .NET Game Server
API_URL = os.environ.get("API_URL", "http://localhost:5000")

# Bot management
NUM_BOTS = 50

# Model persistence
MODEL_DIR = os.environ.get("MODEL_DIR", ".")
MODEL_PATH = os.path.join(MODEL_DIR, "ppo_model.pt")


def _select_device() -> torch.device:
    """Select best available device: CUDA > MPS > CPU."""
    if torch.cuda.is_available():
        dev = torch.device("cuda")
        name = torch.cuda.get_device_name(0)
        logger.info(f"Using CUDA device: {name}")
        return dev
    if hasattr(torch.backends, "mps") and torch.backends.mps.is_available():
        logger.info("Using Apple MPS (Metal Performance Shaders)")
        return torch.device("mps")
    logger.info("No GPU detected, using CPU")
    return torch.device("cpu")


DEVICE = _select_device()

# Network architecture
HIDDEN_SIZES = [256, 256]

# PPO hyperparameters
LEARNING_RATE = 3e-4
GAMMA = 0.99
LAMBDA = 0.95
CLIP_EPSILON = 0.2
ENTROPY_COEFF = 0.01
VALUE_COEFF = 0.5
MAX_GRAD_NORM = 0.5
BUFFER_SIZE = 2048
MINIBATCH_SIZE = 256
EPOCHS = 4

# Observation
OBS_SIZE = 170  # 10 self + 64 food (32 * 2) + 96 players (16 * 6)
ACTION_SIZE = 3  # moveX, moveY, split

# Training loop
TICK_INTERVAL = 0.05  # seconds between state polls (20 TPS)
SAVE_INTERVAL = 60  # seconds between model saves
