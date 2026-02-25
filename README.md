# Agar.IA

An agar.io-style multiplayer cell arena game where AI players learn via PPO (Proximal Policy Optimization) reinforcement learning. Built with .NET 10, SignalR, and vanilla JavaScript with HTML5 Canvas.

![Gameplay](docs/screenshots/gameplay.png)

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js](https://nodejs.org/) (for building frontend assets)

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

# Build and run
dotnet build AgarIA.slnx
dotnet run --project AgarIA.Web
```

The server starts on a local port (shown in the terminal). Open the URL in your browser to play.

### Docker

Pull the pre-built image from Docker Hub:

```bash
docker run -p 5274:5274 -v agaria-data:/app/data daniel3303/agaria:latest
```

The volume mounts to `/app/data`, persisting the SQLite database (`admin.db`) and PPO policy files (`ppo_policy_*.json`) across container restarts.

Open `http://localhost:5274` in your browser.

## How to Play

![Join Screen](docs/screenshots/join-screen.png)

Enter a username and click **PLAY** to join, or **SPECTATE** to watch the AI bots evolve.

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

In spectate mode you can observe the AI bots competing and evolving in real time.

- **WASD / Arrow keys** — Pan the camera
- **Mouse wheel** — Zoom in/out
- **Countdown timer HUD** — Shows time remaining (MaxTime reset) or score threshold (MaxScore reset)
- **Reset at score** — Automatically reset the game when any player reaches a score threshold (forces new generations)
- **Max Speed** — Run the simulation without tick delay for faster evolution
## Admin Dashboard

![Admin Dashboard](docs/screenshots/admin-dashboard.png)

The project includes a password-protected admin panel at `/admin/` for monitoring the simulation, adjusting game settings, and viewing round history. Default credentials: **admin** / **admin**.

## AI Architecture

### Actor-Critic Network

Each tier shares a single ActorCriticNetwork — all bots of that tier use the same policy weights. The network has configurable shared hidden layers branching into two heads:

- **Easy** `(E)` — default: 1×64 hidden neurons
- **Medium** `(M)` — default: 1×128 hidden neurons
- **Hard** `(H)` — default: 2×128 hidden neurons

```
Easy:   151 inputs → [64] hidden (tanh) → policy head (3) + value head (1)
Medium: 151 inputs → [128] hidden (tanh) → policy head (3) + value head (1)
Hard:   151 inputs → [128, 128] hidden (tanh) → policy head (3) + value head (1)
```

The hidden layer architecture for each tier is configurable from the admin Settings page (e.g. "128,64" for two hidden layers). Changing a tier's architecture deletes its policy file and resets training.

#### Input Features (151)

| Feature Group | Count | Description |
|---------------|-------|-------------|
| Self info | 7 | Mass relative to largest, absolute mass (1/mass), can-split flag, two largest cells' positions |
| Nearest food | 64 | 32 closest food items (dx, dy each) |
| Nearest players | 80 | 16 closest players (dx, dy, relative mass, vx, vy each) |

#### Action Space

| Output | Type | Description |
|--------|------|-------------|
| moveX, moveY | Continuous (Gaussian) | Movement direction mean (tanh of raw output). Two learnable LogStd parameters control exploration noise |
| split | Discrete (Bernoulli) | Split probability via sigmoid of logit |

### PPO Training

Each tier has its own PPOTrainer that collects per-tick transitions and trains via Proximal Policy Optimization:

1. **Transition collection**: Every tick, each bot's state, sampled action, reward, value estimate, and log probability are stored in the tier's trajectory buffer
2. **Training trigger**: When the buffer reaches `BufferSize` (default 2048) transitions, a PPO update runs
3. **GAE-λ advantages**: Generalized Advantage Estimation with γ=0.99, λ=0.95 computes per-step advantages, then normalizes to zero mean / unit std
4. **Clipped surrogate updates**: For K epochs (default 4), shuffle buffer into minibatches (default 256), compute ratio of new/old policy probability, apply clipped surrogate loss with ε=0.2
5. **Adam optimizer**: Updates all network parameters (shared layers + policy head + value head + LogStd) with gradient clipping (max norm 0.5)

#### Per-Tick Reward Function

| Component | Value | Description |
|-----------|-------|-------------|
| **Food eaten** | +1.0 per food | Delta of food eaten this tick |
| **Player kills** | +2.0 per kill | Delta of players killed (Easy tier: 0 — food-only reward) |
| **Survival** | +0.01 | Small per-tick bonus for staying alive |
| **Exploration** | +0.05 | Bonus for entering a new grid cell (4×4 grid = 16 cells) |
| **Death** | −5.0 | Terminal penalty on bot death |

#### Hyperparameters

All configurable from the admin Settings page:

| Parameter | Default | Description |
|-----------|---------|-------------|
| BufferSize | 2048 | Transitions collected before training |
| MinibatchSize | 256 | Samples per gradient update |
| Epochs | 4 | Passes over buffer per training cycle |
| Learning Rate | 3×10⁻⁴ | Adam optimizer step size |
| Clip Epsilon | 0.2 | PPO clipping range |
| Entropy Coeff | 0.01 | Entropy bonus weight (encourages exploration) |
| γ (Gamma) | 0.99 | Discount factor |
| λ (Lambda) | 0.95 | GAE trace decay |
| Value Coeff | 0.5 | Value loss weight |
| Max Grad Norm | 0.5 | Gradient clipping threshold |

### Policy Persistence

PPO policy weights + Adam optimizer state auto-save to `ppo_policy_easy.json`, `ppo_policy_medium.json`, and `ppo_policy_hard.json` every 60 seconds. Each tier has its own file. These persist across server restarts, allowing training to continue across sessions. Delete them to start fresh.

## Tech Stack

- **Backend**: .NET 10 (ASP.NET Core)
- **Real-time**: SignalR WebSockets
- **Frontend**: Vanilla JavaScript, HTML5 Canvas
- **AI**: Custom actor-critic neural network + PPO reinforcement learning (no ML frameworks)
- **Admin UI**: Tailwind CSS v4, DaisyUI v5, Vite