# Python AI Sidecar

External AI training process for AgarIA using PyTorch + PPO.

## Setup

```bash
cd AgarIA.Core.AI/python-ai
python -m venv venv
source venv/bin/activate  # Linux/macOS
pip install -r requirements.txt
```

### GPU Support

Device is auto-detected at startup with logging. Priority: CUDA > MPS > CPU.

- **NVIDIA (CUDA)**: Install PyTorch with CUDA from https://pytorch.org
- **AMD (ROCm)**: ROCm exposes as `torch.cuda` in PyTorch (Linux only)
- **Apple Silicon (MPS)**: Automatically detected on macOS with Metal support
- **CPU**: Fallback when no GPU is available

## Usage

1. Start the .NET game server:
   ```bash
   dotnet run --project AgarIA.Web
   ```

2. Start the Python AI:
   ```bash
   cd AgarIA.Core.AI/python-ai
   python train.py
   ```

### Docker

Run with Docker Compose from the project root:

```bash
docker compose up --build
```

Or build the AI image standalone:

```bash
docker build -t agaria-ai ./AgarIA.Core.AI/python-ai
docker run --rm -e API_URL=http://host.docker.internal:5274 agaria-ai
```

For NVIDIA GPU support, uncomment the `deploy` section in `docker-compose.yml`.

## Behavior

The AI will:
- Connect to the game server REST API
- Register 50 bots (configurable in `config.py`)
- Build observation vectors from raw game state
- Run batched inference through a PyTorch neural network
- Train using PPO with GAE-lambda advantages
- Auto-save model to `ppo_model.pt` every 60 seconds
- Re-register bots after game resets

## Configuration

Edit `config.py` or set environment variables:

| Setting | Env Var | Default | Description |
|---------|---------|---------|-------------|
| `API_URL` | `API_URL` | `http://localhost:5000` | Game server URL |
| `MODEL_DIR` | `MODEL_DIR` | `.` | Directory for model checkpoint |
| `NUM_BOTS` | — | 50 | Number of AI bots to register |
| `HIDDEN_SIZES` | — | [256, 256] | Network architecture |

## API Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/ai/state` | Full game state (players, food, tick) |
| GET | `/api/ai/config` | Game constants (map size, speeds, etc.) |
| POST | `/api/ai/players` | Register bots: `{"count": N}` |
| DELETE | `/api/ai/players` | Remove all API-managed bots |
| POST | `/api/ai/actions` | Batch actions: `{"actions": [...]}` |

## Architecture

```
train.py          — Main loop: poll state, infer, post actions, train
client.py         — REST client for .NET game API
features.py       — Feature vector builder (170 features)
model.py          — ActorCriticNetwork (PyTorch)
ppo.py            — PPO trainer with GAE-lambda
normalizer.py     — Observation/reward normalization
config.py         — All configuration
```
