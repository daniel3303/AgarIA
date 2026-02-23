// SignalR client connection and message handling
const Network = (() => {
    let connection = null;
    let onGameUpdate = null;
    let onDied = null;
    let onLeaderboard = null;
    let onReconnected = null;
    let onFitnessStats = null;
    let onResetScores = null;

    // Build SignalR connection to game hub
    function init(callbacks) {
        onGameUpdate = callbacks.onGameUpdate;
        onDied = callbacks.onDied;
        onLeaderboard = callbacks.onLeaderboard;
        onReconnected = callbacks.onReconnected;
        onFitnessStats = callbacks.onFitnessStats;
        onResetScores = callbacks.onResetScores;

        connection = new signalR.HubConnectionBuilder()
            .withUrl("/gamehub")
            .withAutomaticReconnect()
            .build();

        // Register server-to-client handlers
        connection.on("GameUpdate", (data) => {
            if (onGameUpdate) onGameUpdate(data);
        });

        connection.on("Died", (data) => {
            if (onDied) onDied(data);
        });

        connection.on("Leaderboard", (data) => {
            if (onLeaderboard) onLeaderboard(data);
        });

        // Receive fitness stats for spectators
        connection.on("FitnessStats", (data) => {
            if (onFitnessStats) onFitnessStats(data);
        });

        // Receive top scores from the last reset
        connection.on("ResetScores", (data) => {
            if (onResetScores) onResetScores(data);
        });

        connection.onreconnected(() => {
            console.log("Reconnected to server, rejoining...");
            if (onReconnected) onReconnected();
        });

        connection.onclose(() => {
            console.log("Connection closed");
        });

        return connection.start();
    }

    // Send join request with email and username
    function join(username) {
        return connection.invoke("Join", username);
    }

    // Send mouse target position to server
    function move(targetX, targetY) {
        if (connection && connection.state === signalR.HubConnectionState.Connected) {
            connection.invoke("Move", targetX, targetY);
        }
    }

    // Request respawn after death
    function respawn() {
        return connection.invoke("Respawn");
    }

    // Request cell split
    function split() {
        if (connection && connection.state === signalR.HubConnectionState.Connected) {
            connection.invoke("Split");
        }
    }

    // Shoot projectile toward screen position
    function shoot(screenX, screenY) {
        if (connection && connection.state === signalR.HubConnectionState.Connected) {
            connection.invoke("Shoot", screenX, screenY);
        }
    }

    // Join as spectator (no player created)
    function spectate() {
        return connection.invoke("Spectate");
    }

    // Request server reset — kills all players
    function reset() {
        console.log("Sending ResetGame to server");
        if (connection && connection.state === signalR.HubConnectionState.Connected) {
            connection.invoke("ResetGame").then(() => console.log("ResetGame succeeded")).catch(e => console.error("ResetGame failed", e));
        }
    }

    // Update the score threshold that triggers an automatic game reset
    function setResetAtScore(score) {
        if (connection && connection.state === signalR.HubConnectionState.Connected) {
            connection.invoke("SetResetAtScore", score).catch(e => console.error("SetResetAtScore failed", e));
        }
    }

    // Toggle max speed simulation mode
    function setMaxSpeed(enabled) {
        if (connection && connection.state === signalR.HubConnectionState.Connected) {
            connection.invoke("SetMaxSpeed", enabled).catch(e => console.error("SetMaxSpeed failed", e));
        }
    }

    // Set auto reset interval in seconds (0 to disable)
    function setAutoResetSeconds(seconds) {
        if (connection && connection.state === signalR.HubConnectionState.Connected) {
            connection.invoke("SetAutoResetSeconds", seconds).catch(e => console.error("SetAutoResetSeconds failed", e));
        }
    }

    return { init, join, move, respawn, split, shoot, spectate, reset, setResetAtScore, setMaxSpeed, setAutoResetSeconds };
})();
