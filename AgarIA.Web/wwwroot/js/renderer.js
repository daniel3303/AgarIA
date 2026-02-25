// Canvas rendering: clean light background, vibrant blobs, fading trails, grid
const Renderer = (() => {
    let canvas, ctx;
    const MAP_SIZE = 4000;
    const GRID_SIZE = 100;

    // Modern vibrant color palette
    const COLORS = [
        "#6c5ce7",  // purple
        "#00b894",  // green
        "#e17055",  // coral
        "#0984e3",  // blue
        "#fdcb6e",  // yellow
        "#e84393"   // pink
    ];

    // Trail history per player (stores last N positions for fading trail effect)
    const trails = new Map();
    const TRAIL_LENGTH = 20;

    // Smooth radius interpolation per player
    const smoothRadius = new Map();

    // Eat (absorb) particle effects
    const eatEffects = [];
    let prevPlayerMap = new Map();
    let lastRenderTime = 0;

    function init() {
        canvas = document.getElementById("gameCanvas");
        ctx = canvas.getContext("2d");
        resize();
        window.addEventListener("resize", resize);
        return canvas;
    }

    function resize() {
        canvas.width = window.innerWidth;
        canvas.height = window.innerHeight;
    }

    // Lighten a hex color by blending toward white
    function lightenColor(hex, amount) {
        let r, g, b;
        if (hex.length === 4) {
            r = parseInt(hex[1] + hex[1], 16);
            g = parseInt(hex[2] + hex[2], 16);
            b = parseInt(hex[3] + hex[3], 16);
        } else {
            r = parseInt(hex.slice(1, 3), 16);
            g = parseInt(hex.slice(3, 5), 16);
            b = parseInt(hex.slice(5, 7), 16);
        }
        r = Math.round(r + (255 - r) * amount);
        g = Math.round(g + (255 - g) * amount);
        b = Math.round(b + (255 - b) * amount);
        return `rgb(${r},${g},${b})`;
    }

    // Build a map of attract points for engulfing effect: larger cells stretch toward smaller nearby prey
    function getAttractPoints(players) {
        const attractMap = new Map();
        if (!players) return attractMap;
        for (let i = 0; i < players.length; i++) {
            const a = players[i];
            const ra = smoothRadius.get(a.id) ?? a.radius;
            for (let j = i + 1; j < players.length; j++) {
                const b = players[j];
                const rb = smoothRadius.get(b.id) ?? b.radius;
                // Determine which is larger
                let big, bigR, small, smallR;
                if (ra > rb * 1.1) {
                    big = a; bigR = ra; small = b; smallR = rb;
                } else if (rb > ra * 1.1) {
                    big = b; bigR = rb; small = a; smallR = ra;
                } else {
                    continue;
                }
                const dx = small.x - big.x;
                const dy = small.y - big.y;
                const dist = Math.sqrt(dx * dx + dy * dy);
                // Trigger when edge-to-edge gap < 50% of bigger radius (starts early, ramps up)
                const edgeGap = dist - bigR;
                if (edgeGap < bigR * 0.5) {
                    // proximity: 0 = just entering range, 1 = fully overlapping
                    const proximity = Math.min(1, 1 - edgeGap / (bigR * 0.5));
                    if (!attractMap.has(big.id)) attractMap.set(big.id, []);
                    attractMap.get(big.id).push({ x: small.x, y: small.y, radius: smallR, proximity });
                }
            }
        }
        return attractMap;
    }

    // Compute engulfing radius extension for a perimeter point
    function engulfStretch(angle, x, y, radius, attractPoints) {
        if (!attractPoints) return 0;
        let stretch = 0;
        const dirX = Math.cos(angle);
        const dirY = Math.sin(angle);
        for (const ap of attractPoints) {
            const toX = ap.x - x;
            const toY = ap.y - y;
            const len = Math.sqrt(toX * toX + toY * toY) || 1;
            // How much this perimeter direction faces the prey
            const dot = dirX * (toX / len) + dirY * (toY / len);
            if (dot > 0.3) {
                // Smooth ramp: wider angular coverage, stronger effect
                const alignment = (dot - 0.3) / 0.7; // 0..1
                // Up to 35% radius stretch, eased with squared proximity
                stretch += radius * 0.35 * alignment * alignment * ap.proximity * ap.proximity;
            }
        }
        return stretch;
    }

    // Draw an organic wobbly blob shape using sine-wave perimeter distortion
    // attractPoints: optional array of {x, y, radius, proximity} for engulfing stretch
    function drawBlob(ctx, x, y, radius, seed, time, attractPoints) {
        const points = 36;
        const freq = 3;
        ctx.beginPath();
        for (let i = 0; i <= points; i++) {
            const angle = (i / points) * Math.PI * 2;
            const wobble = radius * 0.04 * Math.sin(i * freq + seed + time * 1.5);
            const r = radius + wobble + engulfStretch(angle, x, y, radius, attractPoints);
            const px = x + Math.cos(angle) * r;
            const py = y + Math.sin(angle) * r;
            if (i === 0) {
                ctx.moveTo(px, py);
            } else {
                // Use quadratic curves for smoothness
                const prevAngle = ((i - 0.5) / points) * Math.PI * 2;
                const prevWobble = radius * 0.04 * Math.sin((i - 0.5) * freq + seed + time * 1.5);
                const prevR = radius + prevWobble + engulfStretch(prevAngle, x, y, radius, attractPoints);
                const cpx = x + Math.cos(prevAngle) * prevR;
                const cpy = y + Math.sin(prevAngle) * prevR;
                ctx.quadraticCurveTo(cpx, cpy, px, py);
            }
        }
        ctx.closePath();
    }

    // Simple hash from player ID to get a stable seed
    function idToSeed(id) {
        let hash = 0;
        const str = String(id);
        for (let i = 0; i < str.length; i++) {
            hash = ((hash << 5) - hash) + str.charCodeAt(i);
            hash |= 0;
        }
        return hash;
    }

    // Main render call: clears, draws background/grid, food, trails, then players
    function render(gameState, camera) {
        const time = performance.now() / 1000;
        const dt = lastRenderTime ? time - lastRenderTime : 0.016;
        lastRenderTime = time;

        detectEatEvents(gameState.players);

        ctx.clearRect(0, 0, canvas.width, canvas.height);
        ctx.fillStyle = "#f0f2f5";
        ctx.fillRect(0, 0, canvas.width, canvas.height);

        ctx.save();
        // Camera transform: center viewport on player, apply zoom
        ctx.translate(canvas.width / 2, canvas.height / 2);
        ctx.scale(camera.zoom, camera.zoom);
        ctx.translate(-camera.x, -camera.y);

        drawGrid(camera);
        drawFood(gameState.food, time);
        updateTrails(gameState.players);
        updateSmoothRadius(gameState.players);
        drawTrails();
        drawPlayers(gameState.players, gameState.you, time);
        updateAndDrawEatEffects(dt, gameState.players);
        drawBorder();

        // Bot view range circles
        if (gameState.botViewMeta) {
            const meta = gameState.botViewMeta;
            ctx.setLineDash([12, 8]);
            ctx.lineWidth = 2;

            // Food perception range (green)
            ctx.beginPath();
            ctx.arc(meta.botX, meta.botY, meta.foodRadius, 0, Math.PI * 2);
            ctx.strokeStyle = "rgba(0, 184, 148, 0.5)";
            ctx.stroke();

            // Player perception range (blue)
            ctx.beginPath();
            ctx.arc(meta.botX, meta.botY, meta.playerRadius, 0, Math.PI * 2);
            ctx.strokeStyle = "rgba(9, 132, 227, 0.5)";
            ctx.stroke();

            ctx.setLineDash([]);
        }

        ctx.restore();

        // Bot view HUD label
        if (gameState.botViewMeta) {
            ctx.save();
            ctx.font = "bold 18px 'Inter', 'Segoe UI', system-ui, sans-serif";
            ctx.textAlign = "center";
            ctx.textBaseline = "top";
            ctx.fillStyle = "rgba(0, 0, 0, 0.7)";
            const text = `BOT VIEW: ${gameState.botViewMeta.botName}  [B to exit]`;
            const tw = ctx.measureText(text).width;
            ctx.fillRect(canvas.width / 2 - tw / 2 - 12, 8, tw + 24, 32);
            ctx.fillStyle = "#00b894";
            ctx.fillText(text, canvas.width / 2, 14);
            ctx.restore();
        }

        // Minimap
        drawMinimap(gameState, camera);
    }

    // Draw subtle grid lines across the map
    function drawGrid(camera) {
        ctx.strokeStyle = "rgba(0, 0, 0, 0.06)";
        ctx.lineWidth = 1;

        const startX = Math.max(0, Math.floor((camera.x - canvas.width / 2 / camera.zoom) / GRID_SIZE) * GRID_SIZE);
        const endX = Math.min(MAP_SIZE, camera.x + canvas.width / 2 / camera.zoom);
        const startY = Math.max(0, Math.floor((camera.y - canvas.height / 2 / camera.zoom) / GRID_SIZE) * GRID_SIZE);
        const endY = Math.min(MAP_SIZE, camera.y + canvas.height / 2 / camera.zoom);

        for (let x = startX; x <= endX; x += GRID_SIZE) {
            ctx.beginPath();
            ctx.moveTo(x, startY);
            ctx.lineTo(x, endY);
            ctx.stroke();
        }
        for (let y = startY; y <= endY; y += GRID_SIZE) {
            ctx.beginPath();
            ctx.moveTo(startX, y);
            ctx.lineTo(endX, y);
            ctx.stroke();
        }
    }

    // Draw food items as pulsing circles
    function drawFood(food, time) {
        if (!food) return;
        for (const f of food) {
            if (f.ghosted) ctx.globalAlpha = 0.1;
            const color = COLORS[f.colorIndex] || COLORS[0];
            const r = 4.5 + 1.5 * Math.sin(time * 3 + f.x * 0.1 + f.y * 0.1);
            ctx.beginPath();
            ctx.arc(f.x, f.y, r, 0, Math.PI * 2);
            ctx.fillStyle = color;
            ctx.fill();
            if (f.ghosted) ctx.globalAlpha = 1;
        }
    }

    // Record each player's position for trail effect
    function updateTrails(players) {
        if (!players) return;
        const activeIds = new Set();
        for (const p of players) {
            activeIds.add(p.id);
            let trail = trails.get(p.id);
            if (!trail) {
                trail = [];
                trails.set(p.id, trail);
            }
            trail.push({ x: p.x, y: p.y, radius: p.radius, colorIndex: p.colorIndex });
            if (trail.length > TRAIL_LENGTH) trail.shift();
        }
        // Remove trails for players no longer present
        for (const [id] of trails) {
            if (!activeIds.has(id)) trails.delete(id);
        }
    }

    // Update smooth radius interpolation
    function updateSmoothRadius(players) {
        if (!players) return;
        const activeIds = new Set();
        for (const p of players) {
            activeIds.add(p.id);
            const prev = smoothRadius.get(p.id) ?? p.radius;
            smoothRadius.set(p.id, prev + (p.radius - prev) * 0.15);
        }
        for (const [id] of smoothRadius) {
            if (!activeIds.has(id)) smoothRadius.delete(id);
        }
    }

    // Draw fading trail circles behind each player
    function drawTrails() {
        for (const [, trail] of trails) {
            for (let i = 0; i < trail.length - 1; i++) {
                const t = trail[i];
                const opacity = (i / trail.length) * 0.2;
                const color = COLORS[t.colorIndex] || COLORS[0];
                ctx.beginPath();
                ctx.arc(t.x, t.y, t.radius * 0.6, 0, Math.PI * 2);
                ctx.fillStyle = hexToRgba(color, opacity);
                ctx.fill();
            }
        }
    }

    // Draw player blobs with organic shape, gradient fill, and username labels
    function drawPlayers(players, myId, time) {
        if (!players) return;
        const attractMap = getAttractPoints(players);
        for (const p of players) {
            if (p.ghosted) ctx.globalAlpha = 0.1;
            const color = COLORS[p.colorIndex] || COLORS[0];
            const seed = idToSeed(p.id);
            const radius = smoothRadius.get(p.id) ?? p.radius;

            // Speed lines when boosting
            if (p.boosting) {
                const trail = trails.get(p.id);
                if (trail && trail.length >= 2) {
                    const last = trail[trail.length - 1];
                    const prev = trail[trail.length - 2];
                    const dx = last.x - prev.x;
                    const dy = last.y - prev.y;
                    const dist = Math.sqrt(dx * dx + dy * dy);
                    if (dist > 0.1) {
                        const ndx = dx / dist;
                        const ndy = dy / dist;
                        // Perpendicular
                        const px = -ndy;
                        const py = ndx;
                        ctx.strokeStyle = hexToRgba(color, 0.15);
                        ctx.lineWidth = 2;
                        for (let li = -1; li <= 1; li++) {
                            const ox = p.x - ndx * radius * 1.2 + px * li * radius * 0.4;
                            const oy = p.y - ndy * radius * 1.2 + py * li * radius * 0.4;
                            ctx.beginPath();
                            ctx.moveTo(ox, oy);
                            ctx.lineTo(ox - ndx * radius * 0.6, oy - ndy * radius * 0.6);
                            ctx.stroke();
                        }
                    }
                }
            }

            // Soft drop shadow using blob shape
            const ap = attractMap.get(p.id) || null;
            drawBlob(ctx, p.x, p.y, radius, seed, time, ap);
            ctx.shadowColor = hexToRgba(color, 0.35);
            ctx.shadowBlur = p.boosting ? 25 : 12;
            ctx.shadowOffsetX = 0;
            ctx.shadowOffsetY = 2;

            // Radial gradient fill (3D sphere look)
            const grad = ctx.createRadialGradient(
                p.x - radius * 0.25, p.y - radius * 0.25, radius * 0.1,
                p.x, p.y, radius
            );
            grad.addColorStop(0, lightenColor(color, 0.3));
            grad.addColorStop(1, color);
            ctx.fillStyle = grad;
            ctx.fill();
            ctx.shadowBlur = 0;
            ctx.shadowOffsetY = 0;

            // Thin outline using blob shape
            drawBlob(ctx, p.x, p.y, radius, seed, time, ap);
            ctx.strokeStyle = hexToRgba(color, 0.3);
            ctx.lineWidth = 2;
            ctx.stroke();

            // Username label
            const fontSize = Math.max(12, radius * 0.4);
            ctx.font = `bold ${fontSize}px 'Inter', 'Segoe UI', system-ui, sans-serif`;
            ctx.textAlign = "center";
            ctx.textBaseline = "middle";
            ctx.strokeStyle = "rgba(255, 255, 255, 0.6)";
            ctx.lineWidth = 3;
            ctx.strokeText(p.username, p.x, p.y);
            ctx.fillStyle = "#1a1a2e";
            ctx.fillText(p.username, p.x, p.y);

            // Bot view indicators (drawn at full alpha even if ghosted)
            if (p.ghosted) ctx.globalAlpha = 1;
            if (p.isLargest) {
                // Crown/star indicator above player
                ctx.font = `${Math.max(16, radius * 0.5)}px sans-serif`;
                ctx.textAlign = "center";
                ctx.fillText("\u2B50", p.x, p.y - radius - 8);
            }
            if (p.isSplitCell) {
                // Chain/link indicator
                ctx.beginPath();
                ctx.arc(p.x, p.y, radius + 4, 0, Math.PI * 2);
                ctx.strokeStyle = "rgba(253, 203, 110, 0.8)";
                ctx.lineWidth = 3;
                ctx.setLineDash([6, 4]);
                ctx.stroke();
                ctx.setLineDash([]);
            }
            if (p.ghosted) ctx.globalAlpha = 1;
        }
    }

    // Draw map boundary
    function drawBorder() {
        ctx.strokeStyle = "rgba(0, 0, 0, 0.1)";
        ctx.lineWidth = 3;
        ctx.setLineDash([12, 6]);
        ctx.strokeRect(0, 0, MAP_SIZE, MAP_SIZE);
        ctx.setLineDash([]);
    }

    // Draw minimap in bottom-left corner
    function drawMinimap(gameState, camera) {
        const size = 160;
        const pad = 16;
        const x = pad;
        const y = canvas.height - size - pad;
        const scale = size / MAP_SIZE;

        // Background
        ctx.save();
        ctx.beginPath();
        ctx.roundRect(x, y, size, size, 10);
        ctx.fillStyle = "rgba(255, 255, 255, 0.85)";
        ctx.fill();
        ctx.strokeStyle = "rgba(0, 0, 0, 0.1)";
        ctx.lineWidth = 1;
        ctx.stroke();
        ctx.clip();

        // Players
        if (gameState.players) {
            for (const p of gameState.players) {
                const px = x + p.x * scale;
                const py = y + p.y * scale;
                const pr = Math.max(2, p.radius * scale);
                const color = COLORS[p.colorIndex] || COLORS[0];

                ctx.beginPath();
                ctx.arc(px, py, pr, 0, Math.PI * 2);
                ctx.fillStyle = color;
                ctx.fill();

                // Highlight current player
                if (p.id === gameState.you) {
                    ctx.strokeStyle = "#fff";
                    ctx.lineWidth = 2;
                    ctx.stroke();
                    ctx.strokeStyle = "#1a1a2e";
                    ctx.lineWidth = 1;
                    ctx.stroke();
                }
            }
        }

        // Viewport rectangle
        const vw = (canvas.width / camera.zoom) * scale;
        const vh = (canvas.height / camera.zoom) * scale;
        const vx = x + (camera.x - canvas.width / 2 / camera.zoom) * scale;
        const vy = y + (camera.y - canvas.height / 2 / camera.zoom) * scale;
        ctx.strokeStyle = "rgba(108, 92, 231, 0.5)";
        ctx.lineWidth = 1.5;
        ctx.strokeRect(vx, vy, vw, vh);

        ctx.restore();
    }

    // Detect eat events by comparing current players with previous frame
    function detectEatEvents(players) {
        if (!players) { prevPlayerMap = new Map(); return; }
        const currentIds = new Set(players.map(p => p.id));
        // Find players that disappeared
        for (const [id, prev] of prevPlayerMap) {
            if (!currentIds.has(id)) {
                // Find nearest larger alive player (the eater)
                let bestDist = Infinity;
                let eater = null;
                for (const p of players) {
                    const dx = p.x - prev.x;
                    const dy = p.y - prev.y;
                    const dist = Math.sqrt(dx * dx + dy * dy);
                    if (p.radius > prev.radius * 0.5 && dist < bestDist) {
                        bestDist = dist;
                        eater = p;
                    }
                }
                if (eater && bestDist < eater.radius * 3) {
                    spawnEatParticles(prev, eater);
                }
            }
        }
        // Update prev map
        prevPlayerMap = new Map();
        for (const p of players) {
            prevPlayerMap.set(p.id, { x: p.x, y: p.y, radius: p.radius, colorIndex: p.colorIndex });
        }
    }

    // Spawn inward-moving particles from eaten player toward eater
    function spawnEatParticles(eaten, eater) {
        const color = COLORS[eaten.colorIndex] || COLORS[0];
        const count = 15;
        for (let i = 0; i < count; i++) {
            const angle = (i / count) * Math.PI * 2 + (Math.random() - 0.5) * 0.5;
            const spread = eaten.radius * (0.5 + Math.random() * 0.5);
            eatEffects.push({
                x: eaten.x + Math.cos(angle) * spread,
                y: eaten.y + Math.sin(angle) * spread,
                targetId: eater.id,
                targetX: eater.x,
                targetY: eater.y,
                color,
                life: 0.4,
                maxLife: 0.4,
                size: 2 + Math.random() * 3
            });
        }
    }

    // Update and draw eat absorb particles
    function updateAndDrawEatEffects(dt, players) {
        // Build lookup for live eater positions
        const playerMap = new Map();
        if (players) for (const p of players) playerMap.set(p.id, p);

        for (let i = eatEffects.length - 1; i >= 0; i--) {
            const e = eatEffects[i];
            e.life -= dt;
            if (e.life <= 0) { eatEffects.splice(i, 1); continue; }
            // Update target position if eater is still alive
            const eater = playerMap.get(e.targetId);
            if (eater) { e.targetX = eater.x; e.targetY = eater.y; }
            // Lerp toward target
            const t = 1 - (e.life / e.maxLife); // 0→1
            const ease = t * t; // accelerate inward
            const dx = e.targetX - e.x;
            const dy = e.targetY - e.y;
            e.x += dx * ease * 0.15;
            e.y += dy * ease * 0.15;
            // Draw
            const alpha = e.life / e.maxLife;
            const size = e.size * alpha;
            ctx.beginPath();
            ctx.arc(e.x, e.y, size, 0, Math.PI * 2);
            ctx.fillStyle = hexToRgba(e.color, alpha * 0.8);
            ctx.fill();
        }
    }

    // Convert hex color to rgba string
    function hexToRgba(hex, alpha) {
        let r, g, b;
        if (hex.length === 4) {
            r = parseInt(hex[1] + hex[1], 16);
            g = parseInt(hex[2] + hex[2], 16);
            b = parseInt(hex[3] + hex[3], 16);
        } else {
            r = parseInt(hex.slice(1, 3), 16);
            g = parseInt(hex.slice(3, 5), 16);
            b = parseInt(hex.slice(5, 7), 16);
        }
        return `rgba(${r},${g},${b},${alpha})`;
    }

    return { init, render };
})();
