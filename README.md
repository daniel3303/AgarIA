# Agar.IA

An agar.io-style multiplayer cell arena game with AI players trained via PPO (Proximal Policy Optimization) reinforcement learning. The game server is built with .NET 10, SignalR, and vanilla JavaScript with HTML5 Canvas. AI training runs as a separate Python sidecar process using PyTorch.

![Gameplay](docs/screenshots/gameplay.png)

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js](https://nodejs.org/) (for building frontend assets)
- [Python 3.10+](https://www.python.org/) (for AI training)
- [PyTorch](https://pytorch.org/) (CPU or GPU)

### Install & Run

```bash
# Clone the repository
git clone https://github.com/daniel3303/AgarIA.git
cd AgarIA

# Install frontend dependencies and build assets
cd AgarIA.Web
npm install
npm run build
cd ..

# Build and run the game server
dotnet build AgarIA.slnx
dotnet run --project AgarIA.Web
```

The server starts on a local port (shown in the terminal). Open the URL in your browser to play.

### Start AI Training (Optional)

```bash
cd AgarIA.Core.AI
python -m venv venv
source venv/bin/activate
pip install -r requirements.txt
python train.py
```

The Python AI sidecar connects to the game server via REST API, registers bots, and trains them using PPO. See `AgarIA.Core.AI/README.md` for details.

### Docker

Pull the pre-built image from Docker Hub:

```bash
docker run -p 8095:8095 -v agaria-data:/app/data daniel3303/agaria:latest
```

The volume mounts to `/app/data`, persisting the SQLite database (`admin.db`) across container restarts.

Open `http://localhost:8095` in your browser.

### Docker Compose (Game + AI Training)

Run both the game server and the Python AI sidecar together:

```bash
docker compose up --build
```

This starts two services:
- **game** — .NET game server on port 8095
- **ai** — Python AI sidecar connecting to the game server, training via PPO

The AI model is persisted in a Docker volume (`ai-models`). To enable NVIDIA GPU acceleration, uncomment the `deploy` section in `docker-compose.yml`.

```bash
docker compose down   # Stop both services
```

## How to Play

![Join Screen](docs/screenshots/join-screen.png)

Enter a username and click **PLAY** to join, or **SPECTATE** to watch the AI bots.

### Controls

| Control | Action |
|---------|--------|
| **Mouse** | Move toward cursor |
| **Space** | Split your cell |

### Game Rules

- Every player starts with **10 mass**
- Eat **food pellets** (+1 mass each) scattered across the 4000x4000 map
- Eat **other players** by being at least **15% larger** (1.15x mass ratio) and overlapping them
- **Splitting** divides your cell in two (minimum 24 mass required, max 4 split cells per player). Split cells merge back after a cooldown (200 ticks / 10 seconds)
- **Mass decays** over time (0.02% per tick) — you must keep eating to maintain size
- Eating food or players grants a brief **speed boost**
- Base speed is 4.0 for all cells regardless of mass

## Spectate Mode

![Spectate Mode](docs/screenshots/spectate-mode.png)

In spectate mode you can observe the AI bots competing in real time.

- **WASD / Arrow keys** — Pan the camera
- **Mouse wheel** — Zoom in/out
- **Countdown timer HUD** — Shows time remaining (MaxTime reset) or score threshold (MaxScore reset)
- **Reset at score** — Automatically reset the game when any player reaches a score threshold
- **Max Speed** — Run the simulation without tick delay for faster training

## Admin Dashboard

![Admin Dashboard](docs/screenshots/admin-dashboard.png)

The project includes a password-protected admin panel at `/admin/` for monitoring the simulation, adjusting game settings, and viewing round history. Default credentials: **admin** / **admin**.

## Architecture

### System Overview

```
┌──────────────────────┐       REST API        ┌─────────────────────┐
│  .NET Game Server     │ ◄──────────────────► │  Python AI Sidecar   │
│                       │                       │                      │
│  GameEngine (20 TPS)  │ GET  /api/ai/state    │  PyTorch + PPO       │
│  Heuristic bots (.NET)│ POST /api/ai/players  │  Builds own features │
│  API bot lifecycle    │ POST /api/ai/actions   │  Batched inference   │
│  Training toggle      │ GET/POST /ai/training  │  GPU training        │
│                       │ DEL  /api/ai/players   │                      │
└──────────────────────┘                       └─────────────────────┘
```

The .NET server runs the game loop, physics, and collisions. AI training is handled by an external Python process that communicates via REST API. This separation allows:
- GPU-accelerated training with PyTorch
- Independent iteration on AI architecture without recompiling .NET
- Full flexibility over feature engineering and reward functions

### .NET Game Server

Heuristic bots run inside .NET, providing baseline opponents. The server exposes REST endpoints for external AI control:

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/ai/state` | Full game state (players, food, tick) |
| GET | `/api/ai/config` | Game constants (map size, speeds, etc.) |
| POST | `/api/ai/players` | Register bots: `{"count": N}` |
| DELETE | `/api/ai/players` | Remove all API-managed bots |
| POST | `/api/ai/actions` | Batch actions: `{"actions": [...]}` |
| GET | `/api/ai/training` | Training mode status: `{"enabled": true}` |
| POST | `/api/ai/training` | Toggle training: `{"enabled": false}` |
| POST | `/api/ai/stats` | Report PPO training stats |

External bots auto-timeout after 30 seconds without actions.

### Python AI Sidecar

The Python process (`AgarIA.Core.AI/`) handles all AI training:

1. Registers bots via REST API
2. Polls game state each tick
3. Builds 330-feature observation vectors
4. Runs batched inference through a PyTorch actor-critic network
5. Posts actions back to the game server
6. Collects rewards (mass deltas) and trains via PPO

#### Observation Features (330)

| Feature Group | Count | Description |
|---------------|-------|-------------|
| Self info | 10 | Mass ratio, inv mass, can-split, posX/Y, vx/vy, speed boost, split cell relX/relY |
| Nearest food | 128 | 64 closest food (dx, dy) — sorted by distance |
| Nearest players | 192 | 32 most relevant players (dx, dy, relative mass, vx, vy, edibility) — sorted by threat score |

#### Action Space

| Output | Type | Description |
|--------|------|-------------|
| moveX, moveY | Continuous (Gaussian) | Movement direction with learnable std |
| split | Discrete (Bernoulli) | Split probability via sigmoid |

#### PPO Training

- **Buffer**: Per-bot rollout buffer (`STEPS_PER_BOT=64`), 50 bots = 3200 transitions per update
- **GAE-λ advantages**: γ=0.99, λ=0.95, computed per-bot to avoid interleaving
- **Clipped surrogate**: ε=0.2, K=4 epochs, minibatch=256, value function clipping
- **Entropy coefficient**: 0.001
- **Adam optimizer**: LR=3×10⁻⁴, gradient clipping at 0.5
- **Normalization**: Running mean/variance for observations, running std for rewards
- **Model saves**: Auto-saves to `ppo_model.pt` every 60 seconds
- **Training toggle**: Training can be enabled/disabled at runtime via admin dashboard or REST API

#### Per-Tick Reward

| Component | Value | Description |
|-----------|-------|-------------|
| **Mass delta** | (currentMass − prevMass) / StartMass | Main reward signal |
| **Survival bonus** | +0.01 per tick alive | Encourages staying alive |
| **Death** | −player mass / StartMass | Terminal penalty |

## Tech Stack

- **Backend**: .NET 10 (ASP.NET Core)
- **Real-time**: SignalR WebSockets
- **Frontend**: Vanilla JavaScript, HTML5 Canvas
- **AI Training**: Python, PyTorch, PPO reinforcement learning
- **Admin UI**: Tailwind CSS v4, DaisyUI v5, Vite
