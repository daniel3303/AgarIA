# Agar.IA

An agar.io-style multiplayer cell arena game where AI players evolve using genetic algorithms and neural networks. Built with .NET 10, SignalR, and vanilla JavaScript with HTML5 Canvas.

![Gameplay](docs/screenshots/gameplay.png)

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Install & Run

```bash
# Clone the repository
git clone https://github.com/your-username/AgarIA.git
cd AgarIA

# Build
dotnet build AgarIA.slnx

# Run
dotnet run --project AgarIA.Web
```

The server starts on a local port (shown in the terminal). Open the URL in your browser to play.

## How to Play

![Join Screen](docs/screenshots/join-screen.png)

Enter a username and click **PLAY** to join, or **SPECTATE** to watch the AI bots evolve.

### Controls

| Control | Action |
|---------|--------|
| **Mouse** | Move toward cursor |
| **Space** | Split your cell |
| **Click** | Shoot a projectile |

### Game Rules

- Every player starts with **10 mass**
- Eat **food pellets** (+1 mass each) scattered across the 4000x4000 map
- Eat **other players** by being at least **15% larger** (1.15x mass ratio) and overlapping them
- **Splitting** divides your cell in two (minimum 24 mass required, max 4 split cells per player). Split cells merge back after a cooldown (200 ticks / 10 seconds)
- **Shooting** fires a projectile that costs 1 mass. Hitting a smaller player rewards mass proportional to the size difference (up to 20)
- **Mass decays** over time (0.02% per tick) — you must keep eating to maintain size
- Eating food or players grants a brief **speed boost**
- Base speed is 4.0, with heavier cells moving slower: `speed = baseSpeed / sqrt(mass / 10)`

## Spectate Mode

![Spectate Mode](docs/screenshots/spectate-mode.png)

In spectate mode you can observe the AI bots competing and evolving in real time.

- **WASD / Arrow keys** — Pan the camera
- **Mouse wheel** — Zoom in/out
- **Reset at score** — Automatically reset the game when any player reaches a score threshold (forces new generations)
- **Max Speed** — Run the simulation without tick delay for faster evolution
## Admin Dashboard

![Admin Dashboard](docs/screenshots/admin-dashboard.png)

The project includes a password-protected admin panel at `/admin/` for monitoring the simulation, adjusting game settings, and viewing round history.

## AI Architecture

### Neural Network

Each AI bot is controlled by a feedforward neural network with 1 hidden layer. Bots are split 50/50 into two tiers:

- **Easy** `(E)` — 64 hidden neurons, genome size: **10,694 weights**
- **Medium** `(M)` — 128 hidden neurons, genome size: **21,390 weights**

```
Easy:   161 inputs → 64 hidden (tanh) → 6 outputs
Medium: 161 inputs → 128 hidden (tanh) → 6 outputs
```

#### Input Features (161)

| Feature Group | Count | Description |
|---------------|-------|-------------|
| Self info | 7 | Mass relative to largest, absolute mass (1/mass), can-split flag, two largest cells' positions |
| Nearest food | 64 | 32 closest food items (dx, dy each) |
| Nearest players | 80 | 16 closest players (dx, dy, relative mass, vx, vy each) |
| Nearest projectiles | 10 | 5 closest projectiles (dx, dy each) |

#### Output Decisions (6)

| Output | Description |
|--------|-------------|
| 0-1 | Movement direction (continuous X, Y via tanh, scaled to ±200px offset) |
| 2 | Split decision (> 0.5 triggers split) |
| 3 | Shoot decision (> 0.5 triggers shoot) |
| 4-5 | Shoot direction (dx, dy) |

### Genetic Algorithm

Each tier has its own independent genetic algorithm with a pool of **200 genomes**:

- **Selection**: Tournament selection (pick 12 random, keep the fittest)
- **Crossover**: 70% chance — each weight randomly inherited from either parent
- **Mutation**: 10% chance per weight — Gaussian noise (sigma = 0.3)
- **Pool management**: When pool exceeds 200, the lowest-fitness genome is removed. Duplicate genomes are prevented — if a genome already exists in the pool, only the higher fitness is kept
- **Live checkpoints**: Every 30 seconds, all live bots report their current fitness to the pool. This ensures long-surviving dominant bots keep their genomes competitive without waiting until death

#### Fitness Decay

Every 30 seconds, all existing pool entries are multiplied by **0.95**. This prevents old genomes that scored high when competition was weak from permanently dominating the pool. After 5 minutes a genome's stored fitness is at ~60%, after 15 minutes ~21%, forcing gradual turnover while keeping scores meaningful during gameplay. Live bot checkpoints counterbalance this decay — a bot that keeps growing will keep refreshing its genome's fitness in the pool.

### Fitness Function

The fitness function is designed to reward aggressive, efficient play:

```
fitness = (score + playerMassEaten) × (1 / sqrt(aliveTicks)) × monopolyPenalty
```

| Component | Description |
|-----------|-------------|
| **score** | Final mass at death (already includes mass eaten from players) |
| **playerMassEaten** | Mass gained specifically from eating other players — counted again as a 2x bonus to reward aggression |
| **1 / sqrt(aliveTicks)** | Time efficiency factor — rewards bots that gain mass quickly rather than surviving passively |
| **monopolyPenalty** | `1.0 - killerMassShare` — if the killer had most of the total mass, the victim's genome isn't penalized as harshly |

### Genome Persistence

Evolved neural network weights auto-save to `ai_genomes_easy.json` and `ai_genomes_medium.json` every 60 seconds. Each tier has its own genome file. These files persist across server restarts, allowing evolution to continue across sessions. Delete them to start fresh.

## Tech Stack

- **Backend**: .NET 10 (ASP.NET Core)
- **Real-time**: SignalR WebSockets
- **Frontend**: Vanilla JavaScript, HTML5 Canvas
- **AI**: Custom neural network + genetic algorithm (no ML frameworks)
- **Admin UI**: Tailwind CSS v4, DaisyUI v5, Vite