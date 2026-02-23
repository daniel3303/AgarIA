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

    // Main render call: clears, draws background/grid, food, trails, then players
    function render(gameState, camera) {
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        ctx.fillStyle = "#f0f2f5";
        ctx.fillRect(0, 0, canvas.width, canvas.height);

        ctx.save();
        // Camera transform: center viewport on player, apply zoom
        ctx.translate(canvas.width / 2, canvas.height / 2);
        ctx.scale(camera.zoom, camera.zoom);
        ctx.translate(-camera.x, -camera.y);

        drawGrid(camera);
        drawFood(gameState.food);
        updateTrails(gameState.players);
        drawTrails();
        drawProjectiles(gameState.projectiles);
        drawPlayers(gameState.players, gameState.you);
        drawBorder();

        ctx.restore();

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

    // Draw food items as small solid circles
    function drawFood(food) {
        if (!food) return;
        for (const f of food) {
            const color = COLORS[f.colorIndex] || COLORS[0];
            ctx.beginPath();
            ctx.arc(f.x, f.y, 5, 0, Math.PI * 2);
            ctx.fillStyle = color;
            ctx.fill();
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

    // Draw projectiles as bright glowing dots
    function drawProjectiles(projectiles) {
        if (!projectiles) return;
        for (const p of projectiles) {
            // Outer glow
            ctx.beginPath();
            ctx.arc(p.x, p.y, 12, 0, Math.PI * 2);
            ctx.fillStyle = "rgba(255, 80, 80, 0.15)";
            ctx.fill();

            // Core
            ctx.beginPath();
            ctx.arc(p.x, p.y, 6, 0, Math.PI * 2);
            ctx.fillStyle = "#ff4444";
            ctx.shadowColor = "rgba(255, 68, 68, 0.9)";
            ctx.shadowBlur = 16;
            ctx.fill();
            ctx.shadowBlur = 0;

            // Bright center
            ctx.beginPath();
            ctx.arc(p.x, p.y, 2.5, 0, Math.PI * 2);
            ctx.fillStyle = "#ffffff";
            ctx.fill();
        }
    }

    // Draw player blobs with soft shadow and username labels
    function drawPlayers(players, myId) {
        if (!players) return;
        for (const p of players) {
            const color = COLORS[p.colorIndex] || COLORS[0];

            // Soft drop shadow
            ctx.beginPath();
            ctx.arc(p.x, p.y, p.radius, 0, Math.PI * 2);
            ctx.fillStyle = color;
            ctx.shadowColor = hexToRgba(color, 0.35);
            ctx.shadowBlur = p.boosting ? 25 : 12;
            ctx.shadowOffsetX = 0;
            ctx.shadowOffsetY = 2;
            ctx.fill();
            ctx.shadowBlur = 0;
            ctx.shadowOffsetY = 0;

            // Light inner highlight
            ctx.beginPath();
            ctx.arc(p.x - p.radius * 0.2, p.y - p.radius * 0.2, p.radius * 0.5, 0, Math.PI * 2);
            ctx.fillStyle = "rgba(255, 255, 255, 0.25)";
            ctx.fill();

            // Thin outline
            ctx.beginPath();
            ctx.arc(p.x, p.y, p.radius, 0, Math.PI * 2);
            ctx.strokeStyle = hexToRgba(color, 0.3);
            ctx.lineWidth = 2;
            ctx.stroke();

            // Username label
            const fontSize = Math.max(12, p.radius * 0.4);
            ctx.font = `bold ${fontSize}px 'Inter', 'Segoe UI', system-ui, sans-serif`;
            ctx.textAlign = "center";
            ctx.textBaseline = "middle";
            ctx.strokeStyle = "rgba(255, 255, 255, 0.6)";
            ctx.lineWidth = 3;
            ctx.strokeText(p.username, p.x, p.y);
            ctx.fillStyle = "#1a1a2e";
            ctx.fillText(p.username, p.x, p.y);
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
