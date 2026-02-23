// Main game entry point: manages state, game loop, UI overlays
(() => {
    let gameState = { players: [], food: [], projectiles: [], you: null, tick: 0 };
    let prevState = null;
    let lastUpdateTime = 0;
    let currentUsername = null;
    let isSpectating = false;
    const camera = { x: 2000, y: 2000, zoom: 1 };
    const spectatorKeys = {};

    const joinOverlay = document.getElementById("joinOverlay");
    const deathOverlay = document.getElementById("deathOverlay");
    const hud = document.getElementById("hud");
    const scoreDisplay = document.getElementById("scoreDisplay");
    const leaderboardList = document.getElementById("leaderboardList");
    const playButton = document.getElementById("playButton");
    const spectateButton = document.getElementById("spectateButton");
    const respawnButton = document.getElementById("respawnButton");

    // Initialize renderer and input
    const canvas = Renderer.init();
    Input.init(canvas);

    // Connect to server then set up UI handlers
    Network.init({
        onGameUpdate: handleGameUpdate,
        onDied: handleDied,
        onLeaderboard: handleLeaderboard,
        onReconnected: handleReconnected,
        onFitnessStats: handleFitnessStats
    }).then(() => {
        console.log("Connected to game server");
    });

    // Handle play button: validate inputs, join game, hide overlay
    playButton.addEventListener("click", () => {
        const username = document.getElementById("usernameInput").value.trim();
        if (!username) return;

        currentUsername = username;
        Network.join(username).then(() => {
            joinOverlay.style.display = "none";
            hud.style.display = "block";
        });
    });

    // Spectate button
    spectateButton.addEventListener("click", () => {
        Network.spectate().then(() => {
            isSpectating = true;
            joinOverlay.style.display = "none";
            hud.style.display = "block";
            scoreDisplay.textContent = "SPECTATING";
            document.getElementById("resetPanel").style.display = "block";
            document.getElementById("fitnessPanel").style.display = "block";
        });
    });

    // Spectator camera controls
    window.addEventListener("keydown", (e) => { spectatorKeys[e.code] = true; });
    window.addEventListener("keyup", (e) => { spectatorKeys[e.code] = false; });
    canvas.addEventListener("wheel", (e) => {
        if (!isSpectating) return;
        e.preventDefault();
        camera.zoom = Math.max(0.1, Math.min(2, camera.zoom - e.deltaY * 0.001));
    }, { passive: false });

    // Allow Enter key to trigger play
    document.getElementById("usernameInput").addEventListener("keydown", (e) => {
        if (e.key === "Enter") playButton.click();
    });

    // Respawn after death
    respawnButton.addEventListener("click", () => {
        Network.respawn().then(() => {
            deathOverlay.style.display = "none";
            hud.style.display = "block";
        });
    });

    // Reset game — kills all players so AI can re-evolve
    document.getElementById("resetButton").addEventListener("click", (e) => {
        e.stopPropagation();
        e.preventDefault();
        Network.reset();
    });

    // Toggle max speed simulation mode
    document.getElementById("maxSpeedToggle").addEventListener("change", (e) => {
        Network.setMaxSpeed(e.target.checked);
    });

    // Apply reset-at-score threshold to server
    document.getElementById("applyResetScore").addEventListener("click", (e) => {
        e.stopPropagation();
        e.preventDefault();
        const score = parseFloat(document.getElementById("resetAtScore").value) || 5000;
        Network.setResetAtScore(score);
    });

    // Re-join after SignalR reconnects with new connection ID
    function handleReconnected() {
        if (isSpectating) {
            Network.spectate().then(() => {
                console.log("Re-registered as spectator");
                prevState = null;
            });
        } else if (currentUsername) {
            Network.join(currentUsername).then(() => {
                console.log("Rejoined as", currentUsername);
                prevState = null;
            });
        }
    }

    // Receive game state from server, store for interpolation
    function handleGameUpdate(data) {
        if (!prevState || !prevState.you) {
            console.log("First game update received:", data.players?.length, "players,", data.food?.length, "food");
        }
        prevState = gameState;
        gameState = data;
        lastUpdateTime = performance.now();
    }

    // Show death screen with killer name and final score
    function handleDied(data) {
        document.getElementById("killedByText").textContent = `Eaten by ${data.killedBy}`;
        document.getElementById("finalScoreText").textContent = `Score: ${data.finalScore}`;
        deathOverlay.style.display = "flex";
        hud.style.display = "none";
    }

    // Update leaderboard list in HUD
    function handleLeaderboard(data) {
        leaderboardList.innerHTML = "";
        data.forEach((entry, i) => {
            const li = document.createElement("li");
            li.innerHTML = `<span class="rank">${i + 1}.</span><span class="name">${escapeHtml(entry.username)}</span><span class="lb-score">${entry.score}</span>`;
            leaderboardList.appendChild(li);
        });
    }

    // Update fitness stats panel with top 3, average, and median
    function handleFitnessStats(data) {
        const top3El = document.getElementById("fitnessTop3");
        top3El.innerHTML = data.top3.map((f, i) =>
            `<div>#${i + 1}: ${f.toFixed(2)}</div>`
        ).join("");
        document.getElementById("fitnessAvg").textContent = data.average.toFixed(2);
        document.getElementById("fitnessMedian").textContent = data.median.toFixed(2);
        document.getElementById("fitnessPool").textContent = data.poolSize;
    }

    // Main render loop at 60fps with interpolation between server ticks
    function gameLoop() {
        requestAnimationFrame(gameLoop);

        // Interpolate positions between ticks for smooth movement
        const renderState = interpolate();

        // Update camera to follow current player
        updateCamera(renderState);

        // Send input to server (skip for spectators)
        if (!isSpectating) Input.update(camera);

        // Draw everything
        Renderer.render(renderState, camera);

        // Update score display
        if (renderState.you) {
            const me = renderState.players.find(p => p.id === renderState.you);
            if (me) {
                scoreDisplay.textContent = `Score: ${me.score}`;
            }
        }
    }

    // Linearly interpolate player and projectile positions between server snapshots,
    // with client-side velocity prediction for projectiles beyond the latest tick
    function interpolate() {
        if (!prevState || !gameState.players) return gameState;

        const tickMs = 1000 / 20; // 50ms per tick
        const elapsed = performance.now() - lastUpdateTime;
        const tRaw = elapsed / tickMs;
        const t = Math.min(tRaw, 1);

        const interpolatedPlayers = gameState.players.map(p => {
            const prev = prevState.players ? prevState.players.find(pp => pp.id === p.id) : null;
            if (!prev) return p;
            return {
                ...p,
                x: prev.x + (p.x - prev.x) * t,
                y: prev.y + (p.y - prev.y) * t
            };
        });

        // Interpolate projectiles using velocity derived from last two snapshots
        const interpolatedProjectiles = (gameState.projectiles || []).map(p => {
            const prev = prevState.projectiles ? prevState.projectiles.find(pp => pp.id === p.id) : null;
            if (!prev) return p;
            // Derive per-tick velocity from the two snapshots
            const vx = p.x - prev.x;
            const vy = p.y - prev.y;
            // Interpolate within tick, extrapolate beyond using velocity prediction
            const tProj = Math.min(tRaw, 2);
            return {
                ...p,
                x: prev.x + vx * tProj,
                y: prev.y + vy * tProj
            };
        });

        return { ...gameState, players: interpolatedPlayers, projectiles: interpolatedProjectiles };
    }

    // Smoothly move camera to center on the player, zoom out as mass grows
    function updateCamera(state) {
        if (isSpectating) {
            const panSpeed = 10 / camera.zoom;
            if (spectatorKeys["KeyW"] || spectatorKeys["ArrowUp"]) camera.y -= panSpeed;
            if (spectatorKeys["KeyS"] || spectatorKeys["ArrowDown"]) camera.y += panSpeed;
            if (spectatorKeys["KeyA"] || spectatorKeys["ArrowLeft"]) camera.x -= panSpeed;
            if (spectatorKeys["KeyD"] || spectatorKeys["ArrowRight"]) camera.x += panSpeed;
            camera.x = Math.max(0, Math.min(4000, camera.x));
            camera.y = Math.max(0, Math.min(4000, camera.y));
            return;
        }

        if (!state.you || !state.players) return;
        const me = state.players.find(p => p.id === state.you);
        if (!me) return;

        const targetZoom = Math.max(0.3, 1 - (me.radius - 12) / 300);
        camera.x += (me.x - camera.x) * 0.1;
        camera.y += (me.y - camera.y) * 0.1;
        camera.zoom += (targetZoom - camera.zoom) * 0.05;
    }

    // Prevent XSS in leaderboard names
    function escapeHtml(text) {
        const div = document.createElement("div");
        div.textContent = text;
        return div.innerHTML;
    }

    // Start the render loop
    gameLoop();
})();
