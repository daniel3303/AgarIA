// Mouse and touch input handling, converts screen coords to world coords
const Input = (() => {
    let mouseX = 0;
    let mouseY = 0;
    let mouseDown = false;
    let lastSendTime = 0;
    let lastShootTime = 0;
    let currentCamera = null;
    const SEND_INTERVAL = 1000 / 15; // Throttle to ~15 sends/sec
    const SHOOT_INTERVAL = 100; // 10 shots/sec when holding mouse

    // Initialize input listeners on canvas
    function init(canvas) {
        // Track mouse position relative to canvas center
        canvas.addEventListener("mousemove", (e) => {
            mouseX = e.clientX;
            mouseY = e.clientY;
        });

        // Hold mouse to auto-shoot at 10x/sec
        canvas.addEventListener("mousedown", (e) => {
            if (e.target !== canvas) return;
            mouseDown = true;
        });
        window.addEventListener("mouseup", () => { mouseDown = false; });

        // Space key triggers cell split
        window.addEventListener("keydown", (e) => {
            if (e.code === "Space") {
                e.preventDefault();
                Network.split();
            }
        });

        // Touch support - use first touch point
        canvas.addEventListener("touchmove", (e) => {
            e.preventDefault();
            const touch = e.touches[0];
            mouseX = touch.clientX;
            mouseY = touch.clientY;
        }, { passive: false });

        canvas.addEventListener("touchstart", (e) => {
            e.preventDefault();
            const touch = e.touches[0];
            mouseX = touch.clientX;
            mouseY = touch.clientY;
        }, { passive: false });
    }

    // Convert screen mouse position to world target, throttle sends to server
    function update(camera) {
        currentCamera = camera;
        const now = Date.now();
        if (now - lastSendTime < SEND_INTERVAL) return;
        lastSendTime = now;

        const worldX = camera.x + (mouseX - window.innerWidth / 2) / camera.zoom;
        const worldY = camera.y + (mouseY - window.innerHeight / 2) / camera.zoom;

        Network.move(worldX, worldY);

        // Auto-shoot while mouse is held
        if (mouseDown && now - lastShootTime >= SHOOT_INTERVAL) {
            lastShootTime = now;
            Network.shoot(worldX, worldY);
        }
    }

    return { init, update };
})();
