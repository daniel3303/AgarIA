#!/usr/bin/env python3
"""Main training loop for the Python AI sidecar."""

import logging
import time
import signal
import sys
import os

import torch
import numpy as np

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
)

import config
from client import GameClient
from features import build_observations, compute_rewards
from model import ActorCriticNetwork
from normalizer import RunningNormalizer, RewardNormalizer
from ppo import RolloutBuffer, ppo_update
from config import STEPS_PER_BOT


def main():
    print(f"Device: {config.DEVICE}")
    print(f"API URL: {config.API_URL}")
    print(f"Bots: {config.NUM_BOTS}")
    print(f"Network: {config.OBS_SIZE} -> {config.HIDDEN_SIZES} -> {config.ACTION_SIZE}")
    print()

    client = GameClient()

    # Get game config
    try:
        game_config = client.get_config()
        start_mass = game_config["startMass"]
        print(f"Connected. Map size: {game_config['mapSize']}, Start mass: {start_mass}")
    except Exception as e:
        print(f"Failed to connect to game server: {e}")
        print(f"Make sure the .NET server is running at {config.API_URL}")
        sys.exit(1)

    # Initialize model
    model = ActorCriticNetwork().to(config.DEVICE)
    optimizer = torch.optim.Adam(model.parameters(), lr=config.LEARNING_RATE)
    obs_normalizer = RunningNormalizer(config.OBS_SIZE)
    reward_normalizer = RewardNormalizer()

    # Register bots first so we know num_bots for buffer
    print(f"Registering {config.NUM_BOTS} bots...")
    bot_ids = client.register_bots(config.NUM_BOTS)
    num_bots = len(bot_ids)
    print(f"Registered {num_bots} bots")

    buffer = RolloutBuffer(num_bots, STEPS_PER_BOT, config.OBS_SIZE)

    # Load saved model if exists
    if os.path.exists(config.MODEL_PATH):
        try:
            checkpoint = torch.load(config.MODEL_PATH, map_location=config.DEVICE, weights_only=False)
            model.load_state_dict(checkpoint["model"])
            optimizer.load_state_dict(checkpoint["optimizer"])
            if "obs_normalizer" in checkpoint:
                obs_normalizer.load_state_dict(checkpoint["obs_normalizer"])
            if "reward_normalizer" in checkpoint:
                reward_normalizer.load_state_dict(checkpoint["reward_normalizer"])
            total_steps = checkpoint.get("total_steps", 0)
            print(f"Loaded model from {config.MODEL_PATH} (step {total_steps})")
        except Exception as e:
            print(f"WARNING: Failed to load checkpoint: {e}")
            print(f"Delete {config.MODEL_PATH} if architecture changed. Starting fresh.")
            total_steps = 0
    else:
        total_steps = 0
        print("Starting with fresh model")

    # Graceful shutdown
    running = True

    def on_signal(sig, frame):
        nonlocal running
        print("\nShutting down...")
        running = False

    signal.signal(signal.SIGINT, on_signal)
    signal.signal(signal.SIGTERM, on_signal)

    prev_masses: dict[str, float] = {bid: start_mass for bid in bot_ids}
    last_save = time.time()
    train_count = 0

    print("Training loop started\n")

    while running:
        loop_start = time.time()

        try:
            # Get state
            state = client.get_state()

            # Check if bots are alive, re-register dead ones
            players_by_id = {p["id"]: p for p in state["players"]}
            alive_bots = [bid for bid in bot_ids if bid in players_by_id and players_by_id[bid]["isAlive"]]

            if len(alive_bots) < len(bot_ids) // 2:
                # Many bots died (probably game reset), re-register
                dead_count = len(bot_ids) - len(alive_bots)
                if dead_count > 0:
                    try:
                        new_ids = client.register_bots(dead_count)
                        # Replace dead bot IDs
                        new_bot_ids = []
                        new_idx = 0
                        for bid in bot_ids:
                            if bid in players_by_id and players_by_id[bid]["isAlive"]:
                                new_bot_ids.append(bid)
                            elif new_idx < len(new_ids):
                                new_bot_ids.append(new_ids[new_idx])
                                prev_masses[new_ids[new_idx]] = start_mass
                                new_idx += 1
                        bot_ids = new_bot_ids
                        # Refresh state
                        state = client.get_state()
                    except Exception:
                        pass

            # Build observations
            raw_obs = build_observations(state, bot_ids)
            obs_normalizer.update(raw_obs)
            obs = obs_normalizer.normalize(raw_obs)

            # Compute rewards from mass deltas
            rewards, prev_masses = compute_rewards(None, state, bot_ids, prev_masses, start_mass)
            reward_normalizer.update(rewards)
            norm_rewards = reward_normalizer.normalize(rewards)

            # Determine which bots are done (dead)
            dones = np.array(
                [
                    0.0 if bid in players_by_id and players_by_id[bid]["isAlive"] else 1.0
                    for bid in bot_ids
                ],
                dtype=np.float32,
            )

            # Get actions from model
            obs_t = torch.from_numpy(obs).to(config.DEVICE)
            with torch.no_grad():
                actions, log_probs, values = model.get_action(obs_t)

            actions_np = actions.cpu().numpy()
            log_probs_np = log_probs.cpu().numpy()
            values_np = values.cpu().numpy()

            # Send actions to game
            action_list = []
            for i, bid in enumerate(bot_ids):
                if bid not in players_by_id or not players_by_id[bid]["isAlive"]:
                    continue
                p = players_by_id[bid]
                target_x = max(0, min(p["x"] + float(actions_np[i, 0]) * 200, game_config["mapSize"]))
                target_y = max(0, min(p["y"] + float(actions_np[i, 1]) * 200, game_config["mapSize"]))
                split = bool(actions_np[i, 2] > 0.5)
                action_list.append({
                    "playerId": bid,
                    "targetX": target_x,
                    "targetY": target_y,
                    "split": split,
                })

            if action_list:
                client.post_actions(action_list)

            # Store transitions
            buffer.add(obs, actions_np, log_probs_np, norm_rewards, values_np, dones)
            total_steps += len(bot_ids)

            # Train if buffer full
            if buffer.ready():
                stats = ppo_update(model, optimizer, buffer)
                train_count += 1
                avg_reward = rewards.mean()
                alive_count = int((1 - dones).sum())
                print(
                    f"[Train {train_count}] step={total_steps} "
                    f"loss={stats['loss']:.4f} policy={stats['policy_loss']:.4f} "
                    f"value={stats['value_loss']:.4f} entropy={stats['entropy']:.4f} "
                    f"reward={avg_reward:.4f} alive={alive_count}/{len(bot_ids)}"
                )

            # Save periodically
            if time.time() - last_save > config.SAVE_INTERVAL:
                save_model(model, optimizer, obs_normalizer, reward_normalizer, total_steps)
                last_save = time.time()

        except KeyboardInterrupt:
            break
        except Exception as e:
            print(f"Error: {e}")
            time.sleep(1)
            continue

        # Throttle to match game tick rate
        elapsed = time.time() - loop_start
        if elapsed < config.TICK_INTERVAL:
            time.sleep(config.TICK_INTERVAL - elapsed)

    # Cleanup
    print("Saving model...")
    save_model(model, optimizer, obs_normalizer, reward_normalizer, total_steps)
    print("Removing bots...")
    try:
        client.remove_bots()
    except Exception:
        pass
    print("Done.")


def save_model(model, optimizer, obs_normalizer, reward_normalizer, total_steps):
    torch.save(
        {
            "model": model.state_dict(),
            "optimizer": optimizer.state_dict(),
            "obs_normalizer": obs_normalizer.state_dict(),
            "reward_normalizer": reward_normalizer.state_dict(),
            "total_steps": total_steps,
        },
        config.MODEL_PATH,
    )
    print(f"Model saved to {config.MODEL_PATH} (step {total_steps})")


if __name__ == "__main__":
    main()
