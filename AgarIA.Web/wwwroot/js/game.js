// Main game entry point: manages state, game loop, UI overlays
(() => {
    let gameState = { players: [], food: [], you: null, tick: 0 };
    let prevState = null;
    let lastUpdateTime = 0;
    let currentUsername = null;
    let isSpectating = false;
    let botViewEnabled = false;
    let followedBotId = null;
    let botViewData = null;
    let firstUpdateReceived = false;
    let myConnectionId = null;
    const foodMap = new Map(); // id -> {id, x, y, colorIndex}
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
        onFitnessStats: handleFitnessStats,
        onResetScores: handleResetScores,
        onBotViewUpdate: handleBotViewUpdate,
        onGameReset: handleGameReset,
        onYouAre: handleYouAre
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

    // Spectate button — view-only mode (canvas + leaderboard only)
    spectateButton.addEventListener("click", () => {
        Network.spectate().then(() => {
            isSpectating = true;
            joinOverlay.style.display = "none";
            hud.style.display = "block";
            scoreDisplay.textContent = "SPECTATING";
            document.getElementById("controls").style.display = "none";
        });
    });

    // Spectator camera controls
    window.addEventListener("keydown", (e) => {
        spectatorKeys[e.code] = true;
        if (e.code === "KeyB" && isSpectating) {
            botViewEnabled = !botViewEnabled;
            if (botViewEnabled) {
                followedBotId = findNearestBot(camera.x, camera.y);
                console.log("Bot view enabled, followedBotId:", followedBotId);
                if (followedBotId) Network.enableBotView(followedBotId);
            } else {
                followedBotId = null;
                botViewData = null;
                Network.disableBotView();
            }
        }
    });
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

    // Receive YouAre message with our connection ID
    function handleYouAre(connectionId) {
        myConnectionId = connectionId;
    }

    // Receive game state from server, store for interpolation
    function handleGameUpdate(data) {
        if (!firstUpdateReceived) {
            firstUpdateReceived = true;
            console.log("First game update received:", data.players?.length, "players");
        }

        // Food delta handling
        if (data.food) {
            // Full sync — replace entire foodMap
            foodMap.clear();
            for (const f of data.food) {
                foodMap.set(f.id, f);
            }
        } else {
            // Delta update
            if (data.addedFood) {
                for (const f of data.addedFood) {
                    foodMap.set(f.id, f);
                }
            }
            if (data.removedFoodIds) {
                for (const id of data.removedFoodIds) {
                    foodMap.delete(id);
                }
            }
        }

        // Build food array from map for rendering
        data.food = Array.from(foodMap.values());

        // Set "you" from YouAre messages
        data.you = myConnectionId;

        // Update reset countdown/info display
        updateResetInfo(data);

        prevState = gameState;
        gameState = data;
        lastUpdateTime = performance.now();
    }

    // Show countdown timer or score threshold based on reset type
    function updateResetInfo(data) {
        const el = document.getElementById("resetInfo");
        if (!el) return;

        if (data.resetType === "MaxTime" && data.resetTicksRemaining > 0) {
            const totalSeconds = Math.ceil(data.resetTicksRemaining / 20);
            const mins = Math.floor(totalSeconds / 60);
            const secs = totalSeconds % 60;
            el.textContent = `Next reset: ${mins}:${secs.toString().padStart(2, "0")}`;
            el.style.display = "";
        } else if (data.resetType === "MaxScore" && data.resetAtScore > 0) {
            el.textContent = `Resets at score ${data.resetAtScore}`;
            el.style.display = "";
        } else {
            el.style.display = "none";
        }
    }

    // Show death screen with killer name and final score
    function handleDied(data) {
        document.getElementById("killedByText").textContent = `Eaten by ${data.killedBy}`;
        document.getElementById("finalScoreText").textContent = `Score: ${data.finalScore}`;
        deathOverlay.style.display = "flex";
        hud.style.display = "none";
    }

    // Handle game reset — show death overlay so player can rejoin
    function handleGameReset() {
        document.getElementById("killedByText").textContent = "Game Reset";
        document.getElementById("finalScoreText").textContent = "";
        deathOverlay.style.display = "flex";
        hud.style.display = "none";
        gameState = null;
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

    // Update fitness stats panel — show top 3 genomes by fitness for Easy and Medium tiers
    function handleFitnessStats(data) {
        if (!data) return;
        updateFitnessTier(data.easy, "Easy");
        updateFitnessTier(data.medium, "Medium");
        updateFitnessTier(data.hard, "Hard");
    }

    function updateFitnessTier(tierData, suffix) {
        if (!tierData) return;
        const updatesEl = document.getElementById("ppoUpdates" + suffix);
        if (!updatesEl) return;
        updatesEl.textContent = tierData.totalUpdates || 0;
        document.getElementById("ppoAvgReward" + suffix).textContent = (tierData.avgReward || 0).toFixed(3);
        document.getElementById("ppoPolicyLoss" + suffix).textContent = (tierData.policyLoss || 0).toFixed(4);
        document.getElementById("ppoValueLoss" + suffix).textContent = (tierData.valueLoss || 0).toFixed(4);
        document.getElementById("ppoEntropy" + suffix).textContent = (tierData.entropy || 0).toFixed(3);
        document.getElementById("ppoBuffer" + suffix).textContent = tierData.bufferFill || 0;
    }

    // Display highest score from each of the previous 10 game resets (most recent first)
    function handleResetScores(data) {
        if (!data || !data.length) return;
        const list = document.getElementById("resetScoresList");
        list.innerHTML = [...data].reverse().map(e =>
            `<li><span class="rs-name">${escapeHtml(e.username)}</span> <span class="rs-score">${e.score}</span></li>`
        ).join("");
    }

    // Store latest bot view perception data from server
    function handleBotViewUpdate(data) {
        console.log("BotViewUpdate received:", data?.botId, "foodIds:", data?.foodIds?.length, "playerIds:", data?.playerIds?.length);
        botViewData = data;
    }

    // Click to select a different bot in bot view
    canvas.addEventListener("click", (e) => {
        if (!botViewEnabled) return;
        const worldX = (e.clientX - canvas.width / 2) / camera.zoom + camera.x;
        const worldY = (e.clientY - canvas.height / 2) / camera.zoom + camera.y;
        const nearest = findNearestBot(worldX, worldY);
        if (nearest) {
            followedBotId = nearest;
            Network.enableBotView(nearest);
        }
    });

    // Find the nearest bot (AI, non-split-cell) to given world coordinates
    function findNearestBot(wx, wy) {
        if (!gameState.players) return null;
        let best = null, bestDist = Infinity;
        for (const p of gameState.players) {
            if (!p.isAI || p.ownerId) continue;
            const d = (p.x - wx) ** 2 + (p.y - wy) ** 2;
            if (d < bestDist) { bestDist = d; best = p.id; }
        }
        return best;
    }

    // Filter render state to show only what the followed bot's neural network perceives (server-driven)
    function applyBotView(renderState) {
        if (!botViewEnabled || !followedBotId) return renderState;

        // Find followed bot (main cell, not split)
        const bot = renderState.players.find(p => p.id === followedBotId);
        if (!bot) {
            // Bot died — pick nearest new bot
            followedBotId = findNearestBot(camera.x, camera.y);
            if (followedBotId) Network.enableBotView(followedBotId);
            return renderState;
        }

        // If no server data yet, return unfiltered
        if (!botViewData || botViewData.botId !== followedBotId) {
            if (performance.now() % 1000 < 20) console.log("applyBotView: no match", "botViewData:", !!botViewData, "botViewData.botId:", botViewData?.botId, "followedBotId:", followedBotId);
            return renderState;
        }

        const bx = bot.x, by = bot.y;
        const botOwnerId = bot.ownerId || bot.id;

        // Use server-provided perception sets
        const nearFood = new Set(botViewData.foodIds);
        const food = (renderState.food || []).map(f => ({ ...f, ghosted: !nearFood.has(f.id) }));

        const nearPlayerIds = new Set(botViewData.playerIds);
        const players = renderState.players.map(p => {
            const pid = p.ownerId || p.id;
            const isSelf = pid === botOwnerId;
            const isNear = nearPlayerIds.has(p.id);
            return {
                ...p,
                ghosted: !isSelf && !isNear,
                isLargest: p.id === botViewData.largestPlayerId,
                isSplitCell: isSelf && p.id !== followedBotId
            };
        });

        return {
            ...renderState,
            players,
            food,
            botViewMeta: { botX: bx, botY: by, foodRadius: botViewData.foodRadius, playerRadius: botViewData.playerRadius, botName: bot.username }
        };
    }

    // Main render loop at 60fps with interpolation between server ticks
    function gameLoop() {
        requestAnimationFrame(gameLoop);

        // Interpolate positions between ticks for smooth movement
        let renderState = interpolate();

        // Apply bot view filter before rendering
        renderState = applyBotView(renderState);

        // Update camera to follow current player (or followed bot)
        updateCamera(renderState);

        // Send input to server (skip for spectators)
        if (!isSpectating) Input.update(camera);

        // Draw everything
        Renderer.render(renderState, camera);

        // Update score display
        if (renderState.you) {
            const me = renderState.players.find(p => p.id === renderState.you);
            if (me) {
                const totalScore = renderState.players
                    .filter(p => p.id === renderState.you || p.ownerId === renderState.you)
                    .reduce((sum, p) => sum + p.score, 0);
                scoreDisplay.textContent = `Score: ${totalScore}`;
            }
        }
    }

    // Linearly interpolate player positions between server snapshots
    function interpolate() {
        if (!prevState || !gameState.players) return gameState;

        const tickMs = 1000 / 20; // 50ms per tick
        const elapsed = performance.now() - lastUpdateTime;
        const t = Math.min(elapsed / tickMs, 1);

        const interpolatedPlayers = gameState.players.map(p => {
            const prev = prevState.players ? prevState.players.find(pp => pp.id === p.id) : null;
            if (!prev) return p;
            return {
                ...p,
                x: prev.x + (p.x - prev.x) * t,
                y: prev.y + (p.y - prev.y) * t
            };
        });

        return { ...gameState, players: interpolatedPlayers };
    }

    // Smoothly move camera to center on the player, zoom out as mass grows
    function updateCamera(state) {
        if (isSpectating) {
            // Bot view: lock camera to followed bot
            if (botViewEnabled && state.botViewMeta) {
                const bot = state.players.find(p => p.id === followedBotId);
                if (bot) {
                    const targetZoom = Math.max(0.3, 1 - (bot.radius - 12) / 300);
                    camera.x += (bot.x - camera.x) * 0.1;
                    camera.y += (bot.y - camera.y) * 0.1;
                    camera.zoom += (targetZoom - camera.zoom) * 0.05;
                    return;
                }
            }
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
