﻿using System;
using System.Threading.Tasks;
using Duality;
using Duality.Drawing;
using Duality.Resources;
using Jazz2.Actors.Enemies;
using Jazz2.Actors.Environment;
using Jazz2.Actors.Solid;
using Jazz2.Game;
using Jazz2.Game.Collisions;
using Jazz2.Game.Events;
using Jazz2.Game.Structs;
using Jazz2.Game.Tiles;
using Jazz2.Game.UI;
using MathF = Duality.MathF;

namespace Jazz2.Actors
{
    public enum PlayerType
    {
        Jazz,
        Spaz,
        Lori,
        Frog
    }

    public partial class Player : ActorBase
    {
        private enum SpecialMoveType
        {
            None,
            Buttstomp,
            Uppercut,
            Sidekick
        }

        public enum Modifier
        {
            None,
            Airboard,
            Copter,
            LizardCopter
        }

        public enum LevelExitingState
        {
            None,
            Waiting,
            Transition,
            Ready
        }

        private const float MaxDashingSpeed = 9f;
        private const float MaxRunningSpeed = 4f;
        private const float MaxVineSpeed = 2f;
        private const float MaxDizzySpeed = 2.4f;
        private const float MaxShallowWaterSpeed = 3.6f;
        private const float Acceleration = 0.2f;
        private const float Deceleration = 0.22f;

        private int index;
        private bool isActivelyPushing, wasActivelyPushing;
        private bool controllable = true;

        private bool wasUpPressed, wasDownPressed, wasJumpPressed, wasFirePressed;

        private float controllableTimeout;

        private PlayerType playerType, playerTypeOriginal;
        private SpecialMoveType currentSpecialMove;
        private bool isAttachedToPole;
        private float copterFramesLeft, fireFramesLeft, pushFramesLeft;
        private LevelExitingState levelExiting;
        private bool isFreefall, inWater, isLifting, isSpring;
        private int inShallowWater = -1;
        private Modifier activeModifier;

        private bool inIdleTransition, inLedgeTransition;
        private MovingPlatform carryingObject;
        private SwingingVine currentVine;
        private bool canDoubleJump = true;

        private int lives, coins, foodEaten;
        private uint score;
        private Vector2 checkpointPos;
        private float checkpointLight;

        private float sugarRushLeft, sugarRushStarsTime;

        private int gems, gemsPitch;
        private float gemsTimer;
        private float bonusWarpTimer;

        private float invulnerableTime;
        private float invulnerableBlinkTime;

        private float idleTime;
        private float keepRunningTime;
        private float lastPoleTime;
        private Point2 lastPolePos;
        private float inTubeTime;
        private float dizzyTime;

        private Hud attachedHud;


        public int Lives => lives;
        public uint Score => score;
        public PlayerType PlayerType => playerType;

        public bool CanBreakSolidObjects => (currentSpecialMove != SpecialMoveType.None || sugarRushLeft > 0f);

        public bool InWater => inWater;

        protected override async Task OnActivatedAsync(ActorActivationDetails details)
        {
            playerTypeOriginal = (PlayerType)details.Params[0];
            playerType = playerTypeOriginal;
            index = details.Params[1];

            switch (playerType) {
                case PlayerType.Jazz:
                    await RequestMetadataAsync("Interactive/PlayerJazz");
                    break;
                case PlayerType.Spaz:
                    await RequestMetadataAsync("Interactive/PlayerSpaz");
                    break;
                case PlayerType.Lori:
                    await RequestMetadataAsync("Interactive/PlayerLori");
                    break;
                case PlayerType.Frog:
                    await RequestMetadataAsync("Interactive/PlayerFrog");
                    break;
            }

            SetAnimation(AnimState.Fall);

            LightEmitter light = AddComponent<LightEmitter>();
            light.Intensity = 1.0f;
            light.RadiusNear = 40;
            light.RadiusFar = 110;

            weaponAmmo = new short[(int)WeaponType.Count];
            weaponUpgrades = new byte[(int)WeaponType.Count];

            weaponAmmo[(int)WeaponType.Blaster] = -1;

            collisionFlags = CollisionFlags.CollideWithTileset | CollisionFlags.CollideWithSolidObjects | CollisionFlags.CollideWithOtherActors | CollisionFlags.ApplyGravitation | CollisionFlags.IsSolidObject;

            health = 5;
            maxHealth = health;
            currentWeapon = WeaponType.Blaster;

            checkpointPos = details.Pos.Xy;
            checkpointLight = api.AmbientLight;
        }

        public void ReceiveLevelCarryOver(ExitType exitType, ref PlayerCarryOver carryOver)
        {
            lives = carryOver.Lives;
            score = carryOver.Score;
            foodEaten = carryOver.FoodEaten;
            currentWeapon = carryOver.CurrentWeapon;

            if (carryOver.Ammo != null) {
                int n = Math.Min(weaponAmmo.Length, carryOver.Ammo.Length);
                for (int i = 0; i < n; i++) {
                    weaponAmmo[i] = carryOver.Ammo[i];
                }
            }

            if (carryOver.WeaponUpgrades != null) {
                int n = Math.Min(weaponUpgrades.Length, carryOver.WeaponUpgrades.Length);
                for (int i = 0; i < n; i++) {
                    weaponUpgrades[i] = carryOver.WeaponUpgrades[i];
                }
            }

            if (exitType == ExitType.Warp || exitType == ExitType.Bonus) {
                PlaySound("WarpOut");

                collisionFlags &= ~CollisionFlags.ApplyGravitation;

                isFreefall = CanFreefall();
                SetPlayerTransition(isFreefall ? AnimState.TransitionWarpOutFreefall : AnimState.TransitionWarpOut, false, true, SpecialMoveType.None, delegate {
                    isInvulnerable = false;
                    collisionFlags |= CollisionFlags.ApplyGravitation;
                    controllable = true;
                });

                attachedHud?.BeginFadeIn(false);
            } else {
                attachedHud?.BeginFadeIn(true);
            }

            // Preload all weapons
            for (int i = 0; i < (int)WeaponType.Count; i++) {
                if (weaponAmmo[(int)currentWeapon] != 0) {
                    PreloadMetadata("Weapon/" + (WeaponType)i);
                }
            }
        }

        public PlayerCarryOver PrepareLevelCarryOver()
        {
            return new PlayerCarryOver {
                Type = playerType,

                Lives = lives,
                Score = score,
                FoodEaten = foodEaten,
                CurrentWeapon = currentWeapon,
                Ammo = weaponAmmo,
                WeaponUpgrades = weaponUpgrades
            };
        }

        public bool OnLevelChanging(ExitType exitType)
        {
            if (activeBird != null) {
                activeBird.FlyAway();
                activeBird = null;
            }

            if (levelExiting != LevelExitingState.None) {
                if (levelExiting == LevelExitingState.Waiting) {
                    if (canJump && speedX < 1f && speedY < 1f) {
                        levelExiting = LevelExitingState.Transition;

                        SetPlayerTransition(AnimState.TransitionEndOfLevel, false, true, SpecialMoveType.None, delegate {
                            renderer.Active = false;
                            attachedHud?.BeginFadeOut(true);
                            levelExiting = LevelExitingState.Ready;
                        });
                        PlaySound("EndOfLevel1");

                        collisionFlags &= ~CollisionFlags.ApplyGravitation;
                        speedX = 0;
                        speedY = 0;
                        externalForceX = 0f;
                        externalForceY = 0f;
                        internalForceY = 0f;
                    } else if (lastPoleTime <= 0f) {
                        // Waiting timeout - use warp transition instead
                        levelExiting = LevelExitingState.Transition;

                        SetPlayerTransition(isFreefall ? AnimState.TransitionWarpInFreefall : AnimState.TransitionWarpIn, false, true, SpecialMoveType.None, delegate {
                            renderer.Active = false;
                            attachedHud?.BeginFadeOut(false);
                            levelExiting = LevelExitingState.Ready;
                        });
                        PlaySound("WarpIn");

                        collisionFlags &= ~CollisionFlags.ApplyGravitation;
                        speedX = 0;
                        speedY = 0;
                        externalForceX = 0f;
                        externalForceY = 0f;
                        internalForceY = 0f;
                    }

                    return false;
                }

                return (levelExiting == LevelExitingState.Ready);
            }

            if (exitType == ExitType.Warp || exitType == ExitType.Bonus || inWater) {
                levelExiting = LevelExitingState.Transition;

                SetPlayerTransition(isFreefall ? AnimState.TransitionWarpInFreefall : AnimState.TransitionWarpIn, false, true, SpecialMoveType.None, delegate {
                    renderer.Active = false;
                    attachedHud?.BeginFadeOut(false);
                    levelExiting = LevelExitingState.Ready;
                });
                PlaySound("WarpIn");

                collisionFlags &= ~CollisionFlags.ApplyGravitation;
                speedX = 0;
                speedY = 0;
                externalForceX = 0f;
                externalForceY = 0f;
                internalForceY = 0f;
            } else {
                levelExiting = LevelExitingState.Waiting;

                if (suspendType != SuspendType.None) {
                    MoveInstantly(new Vector2(0f, 10f), MoveType.Relative, true);
                    suspendType = SuspendType.None;
                }

                collisionFlags |= CollisionFlags.ApplyGravitation;
            }

            controllable = false;
            IsFacingLeft = false;
            isInvulnerable = true;
            copterFramesLeft = 0f;
            pushFramesLeft = 0f;

            // Used for waiting timeout
            lastPoleTime = 300f;

            return false;
        }

        protected override void OnFixedUpdate(float timeMult)
        {
            Hud.ShowDebugText("- Pos.: {" + (int)Transform.Pos.X + "; " + (int)Transform.Pos.Y + "}");
            Hud.ShowDebugText("  Speed: {" + speedX.ToString("F1") + "; " + speedY.ToString("F1") + "}");
            Hud.ShowDebugText("  Force: {" + externalForceX.ToString("F1") + "; " + externalForceY.ToString("F1") + "} " + internalForceY + " | " + ((collisionFlags & CollisionFlags.ApplyGravitation) != 0 ? " G" : "") + (controllable ? " C" : "") + (inWater ? " W" : "") + (canJump ? " J" : ""));
            Hud.ShowDebugText("  A.: " + currentAnimationState + " | T.: " + currentTransitionState + " | S.: " + shieldTime);

            // Process level bounds
            Vector3 lastPos = Transform.Pos;
            Rect levelBounds = api.LevelBounds;
            if (lastPos.X < levelBounds.X) {
                lastPos.X = levelBounds.X;
                Transform.Pos = lastPos;
            } else if (lastPos.X > levelBounds.X + levelBounds.W) {
                lastPos.X = levelBounds.X + levelBounds.W;
                Transform.Pos = lastPos;
            }

            float lastSpeedX = speedX;
            float lastForceX = externalForceX;

            PushSolidObjects(timeMult);

            //base.OnUpdate();
            {
                if (timeMult > 1.25f) {
                    TryStandardMovement(1f);
                    TryStandardMovement(timeMult - 1f);
                } else {
                    TryStandardMovement(timeMult);
                }

                OnUpdateHitbox();

                if (renderer != null && renderer.AnimPaused) {
                    if (frozenTimeLeft <= 0f) {
                        renderer.AnimPaused = false;
                    } else {
                        frozenTimeLeft -= timeMult;
                    }
                }
            }

            FollowCarryingPlatform();
            UpdateAnimation(timeMult, lastPos.X, lastSpeedX, lastForceX);

            CheckSuspendedStatus(lastPos);
            CheckDestructibleTiles(timeMult);
            CheckEndOfSpecialMoves(timeMult);

            OnHandleWater();

            bool areaWeaponAllowed;
            int areaWaterBlock;
            OnHandleAreaEvents(timeMult, out areaWeaponAllowed, out areaWaterBlock);

            // Invulnerability
            if (invulnerableTime > 0f) {
                invulnerableTime -= timeMult;

                if (invulnerableTime <= 0f) {
                    isInvulnerable = false;

                    renderer.AnimHidden = false;

                    if (currentCircleEffectRenderer != null) {
                        SetCircleEffect(false);
                    }
                } else if (currentTransitionState != AnimState.Hurt && currentCircleEffectRenderer == null) {
                    if (invulnerableBlinkTime > 0f) {
                        invulnerableBlinkTime -= timeMult;
                    } else {
                        renderer.AnimHidden ^= true;

                        invulnerableBlinkTime = 3f;
                    }
                } else {
                    renderer.AnimHidden = false;
                }
            }

            // Timers
            if (controllableTimeout > 0f) {
                controllableTimeout -= timeMult;

                if (controllableTimeout <= 0f) {
                    controllable = true;

                    if (isAttachedToPole) {
                        // Something went wrong, detach and try to continue
                        // To prevent stucking
                        for (int i = -1; i > -6; i--) {
                            if (MoveInstantly(new Vector2(speedX, i), MoveType.Relative)) {
                                break;
                            }
                        }

                        collisionFlags |= CollisionFlags.ApplyGravitation;
                        isAttachedToPole = false;
                        wasActivelyPushing = false;

                        controllableTimeout = 4f;
                        lastPoleTime = 10f;
                    }
                }
            }

            if (weaponCooldown > 0f) {
                weaponCooldown -= timeMult;
            }

            if (bonusWarpTimer > 0f) {
                bonusWarpTimer -= timeMult;
            }

            if (lastPoleTime > 0f) {
                lastPoleTime -= timeMult;
            }

            if (gemsTimer > 0f) {
                gemsTimer -= timeMult;

                if (gemsTimer <= 0f) {
                    gemsPitch = 0;
                }
            }

            // Weapons
            if (fireFramesLeft > 0f) {
                fireFramesLeft -= timeMult;

                if (fireFramesLeft <= 0f) {
                    // Play post-fire animation
                    if ((currentAnimationState & (AnimState.Walk | AnimState.Run | AnimState.Dash | AnimState.Crouch | AnimState.Buttstomp | AnimState.Swim | AnimState.Airboard | AnimState.Lift | AnimState.Spring)) == 0 &&
                        currentTransitionState != AnimState.TransitionRunToIdle &&
                        currentTransitionState != AnimState.TransitionDashToIdle &&
                        !isAttachedToPole) {

                        if ((currentAnimationState & AnimState.Hook) == AnimState.Hook) {
                            SetTransition(AnimState.TransitionHookShootToHook, false);
                        } else if ((currentAnimationState & AnimState.Copter) != 0) {
                            SetAnimation(AnimState.Copter);
                            SetTransition(AnimState.TransitionCopterShootToCopter, false);
                        } else if ((currentAnimationState & AnimState.Fall) != 0) {
                            SetTransition(AnimState.TransitionFallShootToFall, false);
                        } else {
                            SetTransition(AnimState.TransitionShootToIdle, false);
                        }
                    }
                }
            }

            // Shield
            if (shieldTime > 0f) {
                shieldTime -= timeMult;

                if (shieldTime <= 0f) {
                    SetShield(ShieldType.None, 0f);
                }
            }

            // Dizziness
            if (dizzyTime > 0f) {
                dizzyTime -= timeMult;
            }

            // Sugar Rush
            if (sugarRushLeft > 0f) {
                sugarRushLeft -= timeMult;

                if (sugarRushLeft > 0f) {
                    if (sugarRushStarsTime > 0f) {
                        sugarRushStarsTime -= timeMult;
                    } else {
                        sugarRushStarsTime = MathF.Rnd.NextFloat(2f, 8f);

                        TileMap tilemap = api.TileMap;
                        if (tilemap != null) {
                            GraphicResource res;
                            if (availableAnimations.TryGetValue("SugarRush", out res)) {
                                Vector3 pos = Transform.Pos;
                                pos.Z -= 30f;

                                Material material = res.Material.Res;
                                Texture texture = material.MainTexture.Res;

                                float speedX = MathF.Rnd.NextFloat(-1f, 1f) * MathF.Rnd.NextFloat(0.4f, 4f);
                                tilemap.CreateDebris(new TileMap.DestructibleDebris {
                                    Pos = pos,
                                    Size = res.Base.FrameDimensions,
                                    Speed = new Vector2(speedX, -1f * MathF.Rnd.NextFloat(2.2f, 4f)),
                                    Acceleration = new Vector2(0f, 0.2f),

                                    Scale = MathF.Rnd.NextFloat(0.1f, 0.5f),
                                    ScaleSpeed = -0.002f,
                                    Angle = MathF.Rnd.NextFloat(MathF.TwoPi),
                                    AngleSpeed = speedX * 0.04f,
                                    Alpha = 1f,
                                    AlphaSpeed = -0.018f,

                                    Time = 160f,

                                    Material = material,
                                    MaterialOffset = texture.LookupAtlas(res.FrameOffset + MathF.Rnd.Next(res.FrameCount))
                                });
                            }
                        }
                    }
                } else {
                    OnAnimationStarted();
                }
            }

            // Copter
            if (activeModifier != Modifier.None) {
                if (activeModifier == Modifier.Copter || activeModifier == Modifier.LizardCopter) {
                    copterFramesLeft -= timeMult;
                    if (copterFramesLeft <= 0) {
                        SetModifier(Modifier.None);
                    }
                }
            }

            // Shallow Water
            if (areaWaterBlock != -1) {
                if (inShallowWater == -1) {
                    Vector3 pos = Transform.Pos;
                    pos.Y = areaWaterBlock;
                    pos.Z -= 2f;

                    Explosion.Create(api, pos, Explosion.WaterSplash);
                    api.PlayCommonSound(this, "WaterSplash", 0.7f, 0.5f);
                }

                inShallowWater = areaWaterBlock;
            } else if (inShallowWater != -1) {
                Vector3 pos = Transform.Pos;
                pos.Y = inShallowWater;
                pos.Z -= 2f;

                Explosion.Create(api, pos, Explosion.WaterSplash);
                api.PlayCommonSound(this, "WaterSplash", 1f, 0.5f);

                inShallowWater = -1;
            }

            // Tube
            if (inTubeTime > 0f) {
                inTubeTime -= timeMult;

                if (inTubeTime <= 0f) {
                    controllable = true;
                    collisionFlags |= (CollisionFlags.ApplyGravitation | CollisionFlags.CollideWithTileset);
                } else {
                    // Skip controls, player is not controllable in tube
                    return;
                }
            }

            // Controls
            // Move
            if (keepRunningTime <= 0f) {
                bool canWalk = (controllable && !isLifting && suspendType != SuspendType.SwingingVine &&
                    (playerType != PlayerType.Frog || !ControlScheme.PlayerActionPressed(index, PlayerActions.Fire)));

                bool isRightPressed;
                if (canWalk && ((isRightPressed = ControlScheme.PlayerActionPressed(index, PlayerActions.Right)) ^ ControlScheme.PlayerActionPressed(index, PlayerActions.Left))) {
                    SetAnimation(currentAnimationState & ~(AnimState.Lookup | AnimState.Crouch));

                    if (dizzyTime > 0f) {
                        IsFacingLeft = isRightPressed;
                    } else {
                        IsFacingLeft = !isRightPressed;
                    }

                    isActivelyPushing = wasActivelyPushing = true;

                    if (dizzyTime > 0f || playerType == PlayerType.Frog) {
                        speedX = MathF.Clamp(speedX + Acceleration * timeMult * (IsFacingLeft ? -1 : 1), -MaxDizzySpeed, MaxDizzySpeed);
                    } else if (inShallowWater != -1) {
                        speedX = MathF.Clamp(speedX + Acceleration * timeMult * (IsFacingLeft ? -1 : 1), -MaxShallowWaterSpeed, MaxShallowWaterSpeed);
                    } else {
                        if (suspendType == SuspendType.None && !inWater && ControlScheme.PlayerActionPressed(index, PlayerActions.Run)) {
                            speedX = MathF.Clamp(speedX + Acceleration * timeMult * (IsFacingLeft ? -1 : 1), -MaxDashingSpeed, MaxDashingSpeed);
                        } else if (suspendType == SuspendType.Vine) {
                            if (wasFirePressed) {
                                speedX = 0f;
                            } else {
                                speedX = MathF.Clamp(speedX + Acceleration * timeMult * (IsFacingLeft ? -1 : 1), -MaxVineSpeed, MaxVineSpeed);
                            }
                        } else if (suspendType != SuspendType.Hook) {
                            speedX = MathF.Clamp(speedX + Acceleration * timeMult * (IsFacingLeft ? -1 : 1), -MaxRunningSpeed, MaxRunningSpeed);
                        }
                    }

                    if (canJump) {
                        wasUpPressed = wasDownPressed = false;
                    }
                } else {
                    speedX = MathF.Max((MathF.Abs(speedX) - Deceleration * timeMult), 0) * (speedX < 0 ? -1 : 1);
                    isActivelyPushing = false;

                    float absSpeedX = MathF.Abs(speedX);
                    if (absSpeedX > 4f) {
                        IsFacingLeft = (speedX < 0f);
                    } else if (absSpeedX < 0.001f) {
                        wasActivelyPushing = false;
                    }
                }
            } else {
                keepRunningTime -= timeMult;

                isActivelyPushing = wasActivelyPushing = true;

                float absSpeedX = MathF.Abs(speedX);
                if (absSpeedX > 1f) {
                    IsFacingLeft = (speedX < 0f);
                } else if (absSpeedX < 0.001f) {
                    keepRunningTime = 0f;
                }
            }

            // ToDo: Debug keys only
#if DEBUG
            if (DualityApp.Keyboard.KeyHit(Duality.Input.Key.T)) {
                WarpToPosition(new Vector2(Transform.Pos.X, Transform.Pos.Y - (DualityApp.Keyboard.KeyPressed(Duality.Input.Key.C) ? 500f : 150f)), false);
            } else if (DualityApp.Keyboard.KeyHit(Duality.Input.Key.G)) {
                WarpToPosition(new Vector2(Transform.Pos.X, Transform.Pos.Y + (DualityApp.Keyboard.KeyPressed(Duality.Input.Key.C) ? 500f : 150f)), false);
            } else if (DualityApp.Keyboard.KeyHit(Duality.Input.Key.F)) {
                WarpToPosition(new Vector2(Transform.Pos.X - (DualityApp.Keyboard.KeyPressed(Duality.Input.Key.C) ? 500f : 150f), Transform.Pos.Y), false);
            } else if (DualityApp.Keyboard.KeyHit(Duality.Input.Key.H)) {
                WarpToPosition(new Vector2(Transform.Pos.X + (DualityApp.Keyboard.KeyPressed(Duality.Input.Key.C) ? 500f : 150f), Transform.Pos.Y), false);
            } else if (DualityApp.Keyboard.KeyHit(Duality.Input.Key.N)) {
                api.InitLevelChange(ExitType.Normal, null);
            } else if (DualityApp.Keyboard.KeyHit(Duality.Input.Key.J)) {
                //coins += 5;
                controllable = true;
            } else if (DualityApp.Keyboard.KeyHit(Duality.Input.Key.U)) {
                attachedHud?.ShowLevelText("\f[s:75]\f[w:95]\f[c:1]\n\n\nCheat activated: \f[c:6]Add Ammo");

                for (int i = 0; i < weaponAmmo.Length; i++) {
                    AddAmmo((WeaponType)i, short.MaxValue);
                }
            } else if (DualityApp.Keyboard.KeyHit(Duality.Input.Key.I)) {
                attachedHud?.ShowLevelText("\f[s:75]\f[w:95]\f[c:1]\n\n\nCheat activated: \f[c:6]Add Sugar Rush");

                BeginSugarRush();
            } else if (DualityApp.Keyboard.KeyHit(Duality.Input.Key.O)) {
                attachedHud?.ShowLevelText("\f[s:75]\f[w:95]\f[c:1]\n\n\nCheat activated: \f[c:6]Add all PowerUps");

                for (int i = 0; i < weaponAmmo.Length; i++) {
                    AddWeaponUpgrade((WeaponType)i, 0x1);
                }
            }
#endif

            if (!controllable) {
                return;
            }

            if (inWater || activeModifier != Modifier.None) {
                bool isDownPressed;
                if (((isDownPressed = ControlScheme.PlayerActionPressed(index, PlayerActions.Down)) ^ ControlScheme.PlayerActionPressed(index, PlayerActions.Up))) {
                    float mult;
                    switch (activeModifier) {
                        case Modifier.Airboard: mult = (isDownPressed ? -1f : 0.2f); break;
                        case Modifier.LizardCopter: mult = (isDownPressed ? -2f : 2f); break;
                        default: mult = (isDownPressed ? -1f : 1f); break;
                    }

                    speedY = MathF.Clamp(speedY - Acceleration * timeMult * mult, -MaxRunningSpeed, MaxRunningSpeed);
                } else {
                    speedY = MathF.Max((MathF.Abs(speedY) - Deceleration * timeMult), 0) * (speedY < 0 ? -1 : 1);
                }
            } else {
                // Look-up
                if (ControlScheme.PlayerActionPressed(index, PlayerActions.Up)) {
                    if (!wasUpPressed && dizzyTime <= 0f) {
                        if ((canJump || (suspendType != SuspendType.None && suspendType != SuspendType.SwingingVine)) && !isLifting && Math.Abs(speedX) < float.Epsilon) {
                            wasUpPressed = true;

                            SetAnimation(AnimState.Lookup | (currentAnimationState & AnimState.Hook));
                        }
                    }
                } else if (wasUpPressed) {
                    wasUpPressed = false;

                    SetAnimation(currentAnimationState & ~AnimState.Lookup);
                }

                // Crouch
                if (ControlScheme.PlayerActionPressed(index, PlayerActions.Down)) {
                    if (suspendType == SuspendType.SwingingVine) {
                        // ToDo
                    } else if (suspendType != SuspendType.None) {
                        wasDownPressed = true;

                        MoveInstantly(new Vector2(0f, 10f), MoveType.Relative, true);
                        suspendType = SuspendType.None;

                        collisionFlags |= CollisionFlags.ApplyGravitation;
                    } else if (!wasDownPressed && dizzyTime <= 0f) {
                        if (canJump) {
                            if (!isLifting && Math.Abs(speedX) < float.Epsilon) {
                                wasDownPressed = true;

                                SetAnimation(AnimState.Crouch);
                            }
                        } else if (playerType != PlayerType.Frog) {
                            wasDownPressed = true;

                            controllable = false;
                            speedX = 0;
                            speedY = 0;
                            internalForceY = 0;
                            externalForceY = 0;
                            collisionFlags &= ~CollisionFlags.ApplyGravitation;
                            currentSpecialMove = SpecialMoveType.Buttstomp;
                            SetAnimation(AnimState.Buttstomp);
                            SetPlayerTransition(AnimState.TransitionButtstompStart, true, false, SpecialMoveType.Buttstomp, delegate {
                                speedY = 9;
                                collisionFlags |= CollisionFlags.ApplyGravitation;
                                SetAnimation(AnimState.Buttstomp);
                                PlaySound("Buttstomp", 1f, 0.8f);
                                PlaySound("Buttstomp2");
                            });
                        }
                    }
                } else if (wasDownPressed) {
                    wasDownPressed = false;

                    SetAnimation(currentAnimationState & ~AnimState.Crouch);
                }

                // Jump
                if (ControlScheme.PlayerActionPressed(index, PlayerActions.Jump)) {
                    if (!wasJumpPressed) {
                        wasJumpPressed = true;

                        if (isLifting && canJump && currentSpecialMove == SpecialMoveType.None) {
                            canJump = false;
                            SetAnimation(currentAnimationState & (~AnimState.Lookup & ~AnimState.Crouch));
                            PlaySound("Jump");
                            carryingObject = null;

                            collisionFlags &= ~CollisionFlags.IsSolidObject;

                            isLifting = false;
                            controllable = false;

                            speedY = -3f;
                            internalForceY = 0.86f;

                            collisionFlags &= ~CollisionFlags.CollideWithSolidObjects;

                            SetTransition(AnimState.TransitionLiftEnd, false, delegate {
                                controllable = true;
                                collisionFlags |= CollisionFlags.CollideWithSolidObjects;
                            });
                        } else {

                            switch (playerType) {
                                case PlayerType.Jazz: {
                                    if ((currentAnimationState & AnimState.Crouch) != 0) {
                                        controllable = false;
                                        SetAnimation(AnimState.Uppercut);
                                        SetPlayerTransition(AnimState.TransitionUppercutA, true, true, SpecialMoveType.Uppercut, delegate {
                                            externalForceY = 1.4f;
                                            speedY = -2f;
                                            canJump = false;
                                            SetPlayerTransition(AnimState.TransitionUppercutB, true, true, SpecialMoveType.Uppercut);
                                        });
                                    } else {
                                        if (speedY > 0.01f && !canJump && (currentAnimationState & (AnimState.Fall | AnimState.Copter)) != 0) {
                                            collisionFlags &= ~CollisionFlags.ApplyGravitation;
                                            speedY = 1.5f;
                                            if ((currentAnimationState & AnimState.Copter) == 0) {
                                                SetAnimation(AnimState.Copter);
                                            }
                                            copterFramesLeft = 70;
                                        }
                                    }
                                    break;
                                }
                                case PlayerType.Spaz: {
                                    if ((currentAnimationState & AnimState.Crouch) != 0) {
                                        controllable = false;
                                        SetAnimation(AnimState.Uppercut);
                                        SetPlayerTransition(AnimState.TransitionUppercutA, true, true, SpecialMoveType.Sidekick, delegate {
                                            externalForceX = 8f * (IsFacingLeft ? -1f : 1f);
                                            speedX = 14.4f * (IsFacingLeft ? -1f : 1f);
                                            collisionFlags &= ~CollisionFlags.ApplyGravitation;
                                            SetPlayerTransition(AnimState.TransitionUppercutB, true, true, SpecialMoveType.Sidekick);
                                        });

                                        PlaySound("Sidekick");
                                    } else {
                                        if (!canJump && canDoubleJump) {
                                            canDoubleJump = false;
                                            isFreefall = false;

                                            internalForceY = 1.15f;
                                            speedY = -2f - MathF.Max(0f, (MathF.Abs(speedX) - 4f) * 0.3f);
                                            speedX *= 0.4f;

                                            PlaySound("DoubleJump");

                                            SetTransition(AnimState.Spring, false);
                                        }
                                    }
                                    break;
                                }
                                case PlayerType.Lori: {
                                    if ((currentAnimationState & AnimState.Crouch) != 0) {
                                        controllable = false;
                                        SetAnimation(AnimState.Uppercut);
                                        SetPlayerTransition(AnimState.TransitionUppercutA, true, true, SpecialMoveType.Sidekick, delegate {
                                            externalForceX = 15f * (IsFacingLeft ? -1f : 1f);
                                            speedX = 6f * (IsFacingLeft ? -1f : 1f);
                                            collisionFlags &= ~CollisionFlags.ApplyGravitation;
                                        });
                                    } else {
                                        if (speedY > 0.01f && !canJump && (currentAnimationState & (AnimState.Fall | AnimState.Copter)) != 0) {
                                            collisionFlags &= ~CollisionFlags.ApplyGravitation;
                                            speedY = 1.5f;
                                            if ((currentAnimationState & AnimState.Copter) == 0) {
                                                SetAnimation(AnimState.Copter);
                                            }
                                            copterFramesLeft = 70;
                                        }
                                    }
                                    break;
                                }
                            }

                        }
                    } else {
                        if (suspendType != SuspendType.None) {
                            if (suspendType == SuspendType.SwingingVine) {
                                suspendType = SuspendType.None;
                                currentVine = null;
                                collisionFlags |= CollisionFlags.ApplyGravitation;
                            } else {
                                MoveInstantly(new Vector2(0f, -8f), MoveType.Relative, true);
                            }
                            canJump = true;
                        }
                        if (canJump && currentSpecialMove == SpecialMoveType.None && !ControlScheme.PlayerActionPressed(index, PlayerActions.Down)) {
                            canJump = false;
                            isFreefall = false;
                            SetAnimation(currentAnimationState & (~AnimState.Lookup & ~AnimState.Crouch));
                            PlaySound("Jump");
                            carryingObject = null;

                            // Gravitation is sometimes off because of active copter, turn it on again
                            collisionFlags |= CollisionFlags.ApplyGravitation;

                            collisionFlags &= ~CollisionFlags.IsSolidObject;

                            internalForceY = 1.02f;
                            speedY = -3.55f - MathF.Max(0f, (MathF.Abs(speedX) - 4f) * 0.3f);
                        }
                    }
                } else {
                    if (!wasJumpPressed) {
                        if (internalForceY > 0) {
                            internalForceY = 0;
                        }
                    } else {
                        wasJumpPressed = false;
                    }
                }
            }

            // Fire
            if (weaponAllowed && areaWeaponAllowed && ControlScheme.PlayerActionPressed(index, PlayerActions.Fire)) {
                if (!isLifting && suspendType != SuspendType.SwingingVine && (currentAnimationState & AnimState.Push) == 0 && pushFramesLeft <= 0f) {
                    if (playerType == PlayerType.Frog) {
                        if (currentTransitionState == AnimState.Idle && MathF.Abs(speedX) < 0.1f && MathF.Abs(speedY) < 0.1f && MathF.Abs(externalForceX) < 0.1f && MathF.Abs(externalForceY) < 0.1f) {
                            PlaySound("Tongue", 0.8f);

                            controllable = false;
                            controllableTimeout = 120f;

                            SetTransition(currentAnimationState | AnimState.Shoot, false, delegate {
                                controllable = true;
                                controllableTimeout = 0f;
                            });
                        }
                    } else if (weaponAmmo[(int)currentWeapon] != 0) {
                        if (currentTransitionState == AnimState.Spring || currentTransitionState == AnimState.TransitionShootToIdle) {
                            ForceCancelTransition();
                        }

                        SetAnimation(currentAnimationState | AnimState.Shoot);

                        fireFramesLeft = 20f;

                        if (!wasFirePressed) {
                            wasFirePressed = true;
                            //SetTransition(currentAnimationState | AnimState.TRANSITION_IDLE_TO_SHOOT, false);
                        }

                        FireWeapon();
                    }
                }
            } else if (wasFirePressed) {
                wasFirePressed = false;

                weaponCooldown = 0f;
            }

            if (playerType != PlayerType.Frog && ControlScheme.PlayerActionHit(index, PlayerActions.SwitchWeapon)) {
                SwitchToNextWeapon();
            }
        }

        protected override void OnHitFloor()
        {
            Vector3 pos = Transform.Pos;
            if (api.EventMap.IsHurting(pos.X, pos.Y + 24)) {
                TakeDamage(1, speedX * 0.25f);
            } else if (!inWater && activeModifier == Modifier.None) {
                if (!canJump) {
                    PlaySound("Land", 0.8f);

                    if (MathF.Rnd.NextFloat() < 0.6f) {
                        Explosion.Create(api, pos + new Vector3(0f, 20f, 0f), Explosion.TinyDark);
                    }
                }
            } else {
                // Prevent stucking with water/airboard
                canJump = false;
                if (speedY > 0f) {
                    speedY = 0f;
                }
            }

            canDoubleJump = true;
            isFreefall = false;

            collisionFlags |= CollisionFlags.IsSolidObject;
        }

        protected override void OnHitCeiling()
        {
            Vector3 pos = Transform.Pos;
            if (api.EventMap.IsHurting(pos.X, pos.Y - 4f)) {
                TakeDamage(1, speedX * 0.25f);
            }
        }

        protected override void OnHitWall()
        {
            // Reset speed and show Push animation
            speedX = 0f;
            pushFramesLeft = 2f;

            Vector3 pos = Transform.Pos;
            if (api.EventMap.IsHurting(pos.X + (speedX > 0f ? 1f : -1f) * 16f, pos.Y)) {
                TakeDamage(1, speedX * 0.25f);
            } else {

                if (isActivelyPushing && suspendType == SuspendType.None && !canJump &&
                    currentSpecialMove == SpecialMoveType.None && currentTransitionState != AnimState.TransitionUppercutEnd &&
                    speedY >= -1f && externalForceY <= 0f && copterFramesLeft <= 0f && keepRunningTime <= 0f) {

                    // Character supports ledge climbing
                    if (FindAnimationCandidates(AnimState.TransitionLedgeClimb).Count > 0) {
                        const int maxTolerance = 6;

                        float x = (IsFacingLeft ? -8f : 8f);
                        AABB hitbox1 = AABBInner + new Vector2(x, -42f - maxTolerance);   // Empty space to climb to
                        AABB hitbox2 = AABBInner + new Vector2(x, -42f + 2f);             // Wall below the empty space
                        AABB hitbox3 = AABBInner + new Vector2(x, -42f + 2f + 24f);       // Wall between the player and the wall above (vertically)
                        AABB hitbox4 = AABBInner + new Vector2(x,  20f);                  // Wall below the player
                        AABB hitbox5 = new AABB(AABBInner.LowerBound.X + 2, hitbox1.LowerBound.Y, AABBInner.UpperBound.X - 2, AABBInner.UpperBound.Y); // Player can't climb through walls
                        if ( api.IsPositionEmpty(this, ref hitbox1, false) &&
                            !api.IsPositionEmpty(this, ref hitbox2, false) &&
                            !api.IsPositionEmpty(this, ref hitbox3, false) &&
                            !api.IsPositionEmpty(this, ref hitbox4, false) &&
                             api.IsPositionEmpty(this, ref hitbox5, false)) {

                            ushort[] wallParams = null;
                            if (api.EventMap.GetEventByPosition(IsFacingLeft ? hitbox2.LowerBound.X : hitbox2.UpperBound.X, hitbox2.UpperBound.Y, ref wallParams) != EventType.ModifierNoClimb) {
                                // Move the player upwards, if it is in tolerance, so the animation will look better
                                for (int y = 0; y >= -maxTolerance; y -= 2) {
                                    AABB aabb = AABBInner + new Vector2(x, -42f + y);
                                    if (api.IsPositionEmpty(this, ref aabb, false)) {
                                        MoveInstantly(new Vector2(0f, y), MoveType.Relative, true);
                                        break;
                                    }
                                }

                                // Prepare the player for animation
                                controllable = false;
                                collisionFlags &= ~(CollisionFlags.ApplyGravitation | CollisionFlags.CollideWithTileset | CollisionFlags.CollideWithSolidObjects);

                                speedX = externalForceX = externalForceY = 0f;
                                speedY = -1.36f;
                                pushFramesLeft = fireFramesLeft = copterFramesLeft = 0f;

                                // Stick the player to wall
                                MoveInstantly(new Vector2(IsFacingLeft ? -6f : 6f, 0f), MoveType.Relative, true);

                                SetAnimation(AnimState.Idle);
                                SetTransition(AnimState.TransitionLedgeClimb, false, delegate {
                                    // Reset the player to normal state
                                    canJump = true;
                                    controllable = true;
                                    collisionFlags |= CollisionFlags.ApplyGravitation | CollisionFlags.CollideWithTileset | CollisionFlags.CollideWithSolidObjects;
                                    pushFramesLeft = 0f;
                                    fireFramesLeft = 0f;
                                    copterFramesLeft = 0f;

                                    speedY = 0f;

                                    // Move it far from the ledge
                                    MoveInstantly(new Vector2(IsFacingLeft ? -4f : 4f, 0f), MoveType.Relative);

                                    // Move the player upwards, so it will not be stuck in the wall
                                    for (int y = -2; y > -24; y -= 2) {
                                        if (MoveInstantly(new Vector2(0f, y), MoveType.Relative)) {
                                            break;
                                        }
                                    }
                                });
                            }
                        }
                    }
                }
            }
        }

        protected override void OnUpdateHitbox()
        {
            // ToDo: Figure out how to use hot/coldspots properly.
            // The sprite is always located relative to the hotspot.
            // The coldspot is usually located at the ground level of the sprite,
            // but for falling sprites for some reason somewhere above the hotspot instead.
            // It is absolutely important that the position of the hitbox stays constant
            // to the hotspot, though; otherwise getting stuck at walls happens all the time.
            Vector3 pos = Transform.Pos;

            AABBInner = new AABB(pos.X - 11f, pos.Y + 8f - 12f, pos.X + 11f, pos.Y + 8f + 12f);
        }

        public override bool OnTileDeactivate(int tx1, int ty1, int tx2, int ty2)
        {
            // Player can never be deactivated
            return false;
        }

        protected override bool OnPerish(ActorBase collider)
        {
            if (currentTransitionState != AnimState.TransitionDeath) {
                isInvulnerable = true;

                ForceCancelTransition();

                SetPlayerTransition(AnimState.TransitionDeath, false, true, SpecialMoveType.None, delegate {
                    if (lives > 1 || api.Difficulty == GameDifficulty.Multiplayer) {
                        if (lives > 1) {
                            lives--;
                        }

                        // Remove fast fires
                        weaponUpgrades[(int)WeaponType.Blaster] = (byte)(weaponUpgrades[(int)WeaponType.Blaster] & 0x1);

                        canJump = false;
                        speedX = 0;
                        speedY = 0;
                        externalForceX = 0f;
                        externalForceY = 0f;
                        internalForceY = 0f;
                        fireFramesLeft = 0f;
                        copterFramesLeft = 0f;
                        pushFramesLeft = 0f;
                        weaponCooldown = 0f;
                        controllable = true;
                        inShallowWater = -1;
                        SetModifier(Modifier.None);

                        // Spawn corpse
                        PlayerCorpse corpse = new PlayerCorpse();
                        corpse.OnActivated(new ActorActivationDetails {
                            Api = api,
                            Pos = Transform.Pos,
                            Params = new[] { (ushort)(playerType), (ushort)(IsFacingLeft ? 1 : 0) }
                        });
                        api.AddActor(corpse);

                        SetAnimation(AnimState.Idle);

                        if (api.HandlePlayerDied(this)) {
                            // Reset health
                            health = maxHealth;

                            // Player can be respawned immediately
                            isInvulnerable = false;
                            collisionFlags |= CollisionFlags.ApplyGravitation;

                            // Return to the last save point
                            MoveInstantly(checkpointPos, MoveType.Absolute, true);
                            api.AmbientLight = checkpointLight;
                            api.LimitCameraView(0, 0);
                            api.WarpCameraToTarget(this);

                            if (api.Difficulty != GameDifficulty.Multiplayer) {
                                api.EventMap.RollbackToCheckpoint();
                            }

                        } else {
                            // Respawn is delayed
                            controllable = false;
                            renderer.Active = false;

                            // ToDo: Turn off collisions
                        }
                    } else {
                        controllable = false;
                        renderer.Active = false;

                        api.HandleGameOver();
                    }
                });
            }
            return false;
        }

        protected override void OnAnimationStarted()
        {
            if (sugarRushLeft <= 0f) {
                // Reset renderer
                renderer.CustomMaterial = null;
            } else {
                // Refresh temporary material
                BatchInfo blinkMaterial = new BatchInfo(renderer.SharedMaterial.Res.Info);
                blinkMaterial.Technique = ContentResolver.Current.RequestShader("Colorize");
                blinkMaterial.MainColor = new ColorRgba(0.7f, 0.5f);
                renderer.CustomMaterial = blinkMaterial;
            }
        }

        private void UpdateAnimation(float timeMult, float lastX, float lastSpeedX, float lastForceX)
        {
            if (controllable) {
                float posX = Transform.Pos.X;

                AnimState oldState = currentAnimationState;
                AnimState newState;
                if (inWater) {
                    newState = AnimState.Swim;
                } else if (activeModifier == Modifier.Airboard) {
                    newState = AnimState.Airboard;
                } else if (activeModifier == Modifier.Copter) {
                    newState = AnimState.Copter;
                } else if (activeModifier == Modifier.LizardCopter) {
                    newState = AnimState.Hook;
                } else if (suspendType == SuspendType.SwingingVine) {
                    newState = AnimState.Swing;
                } else if (isLifting) {
                    newState = AnimState.Lift;
                } else if (canJump && isActivelyPushing && pushFramesLeft > 0f) {
                    newState = AnimState.Push;
                } else {
                    // Only certain ones don't need to be preserved from earlier state, others should be set as expected
                    AnimState composite = unchecked(currentAnimationState & (AnimState)0xFFF83F60);

                    if (isActivelyPushing == wasActivelyPushing) {
                        float absSpeedX = MathF.Abs(speedX);
                        if (absSpeedX > MaxRunningSpeed) {
                            composite |= AnimState.Dash;
                        } else if (keepRunningTime > 0f) {
                            composite |= AnimState.Run;
                        } else if (absSpeedX > 0f) {
                            composite |= AnimState.Walk;
                        }

                        if (inIdleTransition) {
                            CancelTransition();
                        }
                    }

                    if (fireFramesLeft > 0f) {
                        composite |= AnimState.Shoot;
                    }

                    if (suspendType != SuspendType.None) {
                        composite |= AnimState.Hook;
                    } else {
                        if (canJump) {
                            // Grounded, no vertical speed
                            if (dizzyTime > 0f) {
                                composite |= AnimState.Dizzy;
                            }
                        } else if (speedY < -float.Epsilon) {
                            // Jumping, ver. speed is negative
                            if (isSpring) {
                                composite |= AnimState.Spring;
                            } else {
                                composite |= AnimState.Jump;
                            }

                        } else if (isFreefall) {
                            // Free falling, ver. speed is positive
                            composite |= AnimState.Freefall;
                            isSpring = false;
                        } else {
                            // Falling, ver. speed is positive
                            composite |= AnimState.Fall;
                            isSpring = false;
                        }
                    }

                    newState = composite;
                }

                if (newState == AnimState.Idle) {
                    if (idleTime > 600f) {
                        idleTime = 0f;

                        if (currentTransitionState == AnimState.Idle) {
                            SetPlayerTransition(AnimState.TransitionIdleBored, true, false, SpecialMoveType.None);
                        }
                    } else {
                        idleTime += timeMult;
                    }
                } else {
                    idleTime = 0f;
                }

                SetAnimation(newState);

                switch (oldState) {
                    case AnimState.Walk:
                        if (newState == AnimState.Idle) {
                            inIdleTransition = true;
                            SetTransition(AnimState.TransitionRunToIdle, true, delegate {
                                inIdleTransition = false;
                            });
                        } else if (newState == AnimState.Dash) {
                            SetTransition(AnimState.TransitionRunToDash, true);
                        }
                        break;
                    case AnimState.Dash:
                        if (newState == AnimState.Idle) {
                            inIdleTransition = true;
                            SetTransition(AnimState.TransitionDashToIdle, true, delegate {
                                inIdleTransition = false;
                                SetTransition(AnimState.TransitionRunToIdle, true);
                            });
                        }
                        break;
                    case AnimState.Fall:
                    case AnimState.Freefall:
                        if (newState == AnimState.Idle) {
                            SetTransition(AnimState.TransitionFallToIdle, true);
                        }
                        break;
                    case AnimState.Idle:
                        if (newState == AnimState.Jump) {
                            SetTransition(AnimState.TransitionIdleToJump, true);
                        } else if (!inLedgeTransition) {
                            AABB aabbL = new AABB(AABBInner.LowerBound.X + 2, AABBInner.UpperBound.Y - 10, AABBInner.LowerBound.X + 4, AABBInner.UpperBound.Y + 28);
                            AABB aabbR = new AABB(AABBInner.UpperBound.X - 4, AABBInner.UpperBound.Y - 10, AABBInner.UpperBound.X - 2, AABBInner.UpperBound.Y + 28);
                            if (IsFacingLeft
                                ? (api.IsPositionEmpty(this, ref aabbL, true) && !api.IsPositionEmpty(this, ref aabbR, true))
                                : (!api.IsPositionEmpty(this, ref aabbL, true) && api.IsPositionEmpty(this, ref aabbR, true))) {

                                inLedgeTransition = true;
                                // ToDo: Spaz's and Lori's animation should be continual
                                SetTransition(AnimState.TransitionLedge, true);

                                PlaySound("Ledge");
                            }
                        } else if (newState != AnimState.Idle) {
                            inLedgeTransition = false;

                            if (currentTransitionState == AnimState.TransitionLedge) {
                                CancelTransition();
                            }
                        }
                        break;
                }
            }
        }

        private void PushSolidObjects(float timeMult)
        {
            if (pushFramesLeft > 0f) {
                pushFramesLeft -= timeMult;
            }

            if (canJump && controllable && isActivelyPushing && MathF.Abs(speedX) > float.Epsilon) {
                ActorBase collider;
                AABB hitbox = AABBInner + new Vector2(speedX < 0 ? -2f : 2f, 0f);
                if (!api.IsPositionEmpty(this, ref hitbox, false, out collider)) {
                    SolidObjectBase solidObject = collider as SolidObjectBase;
                    if (solidObject != null) {
                        collisionFlags &= ~CollisionFlags.IsSolidObject;
                        if (solidObject.Push(speedX < 0, timeMult)) {
                            pushFramesLeft = 3f;
                        }
                        collisionFlags |= CollisionFlags.IsSolidObject;
                    }
                }
            } else if ((collisionFlags & CollisionFlags.IsSolidObject) != 0) {
                ActorBase collider;
                AABB aabb = AABBInner + new Vector2(0f, -2f);
                if (!api.IsPositionEmpty(this, ref aabb, false, out collider)) {
                    SolidObjectBase solidObject = collider as SolidObjectBase;
                    if (solidObject != null) {

                        if (AABBInner.LowerBound.Y >= solidObject.AABBInner.LowerBound.Y && !isLifting) {
                            isLifting = true;

                            SetTransition(AnimState.TransitionLiftStart, true);
                        }
                    } else {
                        isLifting = false;
                    }
                } else {
                    isLifting = false;
                }
            } else {
                isLifting = false;
            }
        }

        private void CheckEndOfSpecialMoves(float timeMult)
        {
            // Buttstomp
            if (currentSpecialMove == SpecialMoveType.Buttstomp && (canJump || suspendType != SuspendType.None)) {
                EndDamagingMove();
                if (suspendType == SuspendType.None && !isSpring) {
                    SetTransition(AnimState.TransitionButtstompEnd, false, delegate {
                        controllable = true;
                    });
                } else {
                    controllable = true;
                }
            }

            // Uppercut
            if (currentSpecialMove == SpecialMoveType.Uppercut && currentTransitionState == AnimState.Idle && ((currentAnimationState & AnimState.Uppercut) != 0) && speedY > -2 && !canJump) {
                EndDamagingMove();
            }

            // Sidekick
            if (currentSpecialMove == SpecialMoveType.Sidekick && currentTransitionState == AnimState.Idle /*&& ((currentAnimationState & AnimState.UPPERCUT) != 0)*/ && MathF.Abs(speedX) < 0.01f) {
                EndDamagingMove();
                controllable = true;
                if (suspendType == SuspendType.None) {
                    SetTransition(AnimState.TransitionUppercutEnd, false);
                }
            }

            // Copter Ears
            if (activeModifier != Modifier.Copter && activeModifier != Modifier.LizardCopter) {
                // ToDo: Is this still needed?
                bool cancelCopter;
                if ((currentAnimationState & AnimState.Copter) != 0) {
                    cancelCopter = (canJump || suspendType != SuspendType.None || copterFramesLeft <= 0f);

                    copterFramesLeft -= timeMult;
                } else {
                    cancelCopter = ((currentAnimationState & AnimState.Fall) != 0 && copterFramesLeft > 0f);
                }

                if (cancelCopter) {
                    copterFramesLeft = 0f;
                    SetAnimation(currentAnimationState & ~AnimState.Copter);
                    if (!isAttachedToPole) {
                        collisionFlags |= CollisionFlags.ApplyGravitation;
                    }
                }
            }
        }

        private void CheckDestructibleTiles(float timeMult)
        {
            TileMap tiles = api.TileMap;
            if (tiles == null) {
                return;
            }

            AABB aabb = AABBInner + new Vector2((speedX + externalForceX) * 2f * timeMult, (speedY - externalForceY) * 2f * timeMult);

            // Buttstomp/etc. tiles checking
            if (currentSpecialMove != SpecialMoveType.None || sugarRushLeft > 0f) {
                int destroyedCount = tiles.CheckSpecialDestructible(ref aabb);
                AddScore((uint)(destroyedCount * 50));

                ActorBase solidObject;
                if (!(api.IsPositionEmpty(this, ref aabb, false, out solidObject)) && solidObject != null) {
                    solidObject.OnHandleCollision(this);
                }
            }

            // Speed tiles checking
            if (MathF.Abs(speedX) > float.Epsilon || MathF.Abs(speedY) > float.Epsilon || sugarRushLeft > 0f) {
                int destroyedCount = tiles.CheckSpecialSpeedDestructible(ref aabb,
                    sugarRushLeft > 0f ? 64f : MathF.Max(MathF.Abs(speedX), MathF.Abs(speedY)));

                AddScore((uint)(destroyedCount * 50));
            }

            tiles.CheckCollapseDestructible(ref aabb);
        }

        private void CheckSuspendedStatus(Vector3 lastPos)
        {
            if (suspendType == SuspendType.SwingingVine) {
                return;
            }

            TileMap tiles = api.TileMap;
            if (tiles == null) {
                return;
            }

            Vector3 pos = Transform.Pos;

            AnimState currentState = currentAnimationState;

            SuspendType newSuspendState = tiles.GetTileSuspendState(pos.X, pos.Y - 1f);

            if (newSuspendState == suspendType) {
                if (newSuspendState == SuspendType.None) {
                    const float tolerance = 6f;

                    newSuspendState = tiles.GetTileSuspendState(pos.X - tolerance, pos.Y - 1f);
                    if (newSuspendState != SuspendType.Hook) {
                        newSuspendState = tiles.GetTileSuspendState(pos.X + tolerance, pos.Y - 1f);
                        if (newSuspendState != SuspendType.Hook) {
                            return;
                        } else {
                            MoveInstantly(new Vector2(tolerance, 0f), MoveType.Relative, true);
                        }
                    } else {
                        MoveInstantly(new Vector2(-tolerance, 0f), MoveType.Relative, true);
                    }
                } else {
                    return;
                }
            }

            if (newSuspendState != SuspendType.None && playerType != PlayerType.Frog) {
                if (currentSpecialMove != SpecialMoveType.Uppercut) {

                    suspendType = newSuspendState;
                    collisionFlags &= ~CollisionFlags.ApplyGravitation;

                    if (speedY > 0 && newSuspendState == SuspendType.Vine) {
                        PlaySound("HookAttach", 0.8f, 1.2f);
                    }

                    speedY = 0f;
                    externalForceY = 0f;
                    isFreefall = false;
                    isSpring = false;
                    copterFramesLeft = 0f;

                    if (newSuspendState == SuspendType.Hook || wasFirePressed) {
                        speedX = externalForceX = 0f;
                    }

                    // Move downwards until we're on the standard height
                    while (tiles.GetTileSuspendState(pos.X, pos.Y /*- 5*/- 1) != SuspendType.None) {
                        MoveInstantly(new Vector2(0f, 1f), MoveType.Relative, true);
                        pos = Transform.Pos;
                    }
                    MoveInstantly(new Vector2(0f, -1f), MoveType.Relative, true);
                }
            } else {
                suspendType = SuspendType.None;
                if ((currentState & (AnimState.Buttstomp | AnimState.Copter)) == 0 && !isAttachedToPole) {
                    collisionFlags |= CollisionFlags.ApplyGravitation;
                }
            }
        }

        private void OnHandleWater()
        {
            if (inWater) {
                if (Transform.Pos.Y >= api.WaterLevel) {
                    collisionFlags &= ~CollisionFlags.ApplyGravitation;

                    if (MathF.Abs(speedX) > 1f || MathF.Abs(speedY) > 1f) {
                        float angle;
                        if (speedX == 0f) {
                            if (IsFacingLeft) {
                                angle = MathF.Atan2(-speedY, -float.Epsilon);
                            } else {
                                angle = MathF.Atan2(speedY, float.Epsilon);
                            }
                        } else if (speedX < 0f) {
                            angle = MathF.Atan2(-speedY, -speedX);
                        } else {
                            angle = MathF.Atan2(speedY, speedX);
                        }

                        if (angle > MathF.Pi) {
                            angle = angle - MathF.TwoPi;
                        }

                        Transform.Angle = MathF.Clamp(angle, -MathF.PiOver3, MathF.PiOver3);
                    }

                    if (currentTransitionState == AnimState.Idle) {
                        renderer.AnimDuration = MathF.Max(currentAnimation.FrameDuration + 1f - new Vector2(speedX, speedY).Length * 0.26f, 0.4f);
                    }

                } else {
                    inWater = false;

                    collisionFlags |= CollisionFlags.ApplyGravitation;
                    canJump = true;
                    externalForceY = 0.45f;
                    Transform.Angle = 0;

                    SetAnimation(AnimState.Jump);

                    Explosion.Create(api, Transform.Pos - new Vector3(0f, 4f, 2f), Explosion.WaterSplash);
                    api.PlayCommonSound(this, "WaterSplash", 1f, 0.5f);
                }
            } else {
                if (Transform.Pos.Y >= api.WaterLevel) {
                    inWater = true;

                    controllable = true;
                    EndDamagingMove();

                    Explosion.Create(api, Transform.Pos - new Vector3(0f, 4f, 2f), Explosion.WaterSplash);
                    api.PlayCommonSound(this, "WaterSplash", 0.7f, 0.5f);
                }
            }
        }

        private void OnHandleAreaEvents(float timeMult, out bool areaWeaponAllowed, out int areaWaterBlock)
        {
            areaWeaponAllowed = true;
            areaWaterBlock = -1;

            EventMap events = api.EventMap;
            if (events == null) {
                return;
            }

            Vector3 pos = Transform.Pos;

            ushort[] p = null;
            EventType tileEvent = events.GetEventByPosition(pos.X, pos.Y, ref p);
            switch (tileEvent) {
                case EventType.LightSet: { // Intensity, Red, Green, Blue, Flicker
                    // ToDo: Change only player view, handle splitscreen multiplayer
                    api.AmbientLight = p[0] * 0.01f;
                    break;
                }
                case EventType.WarpOrigin: { // Warp ID, Fast, Set Lap
                    if (currentTransitionState == AnimState.Idle || currentTransitionState == (AnimState.Dash | AnimState.Jump) || currentTransitionCancellable) {
                        Vector2 c = events.GetWarpTarget(p[0]);
                        if (c.X != -1f && c.Y != -1f) {
                            WarpToPosition(c, p[1] != 0);
                        }
                    }
                    break;
                }
                case EventType.ModifierHPole: {
                    InitialPoleStage(true);
                    break;
                }
                case EventType.ModifierVPole: {
                    InitialPoleStage(false);
                    break;
                }
                case EventType.ModifierTube: { // XSpeed, YSpeed, Wait Time, Trig Sample, Become Noclip, Noclip Only
                    // ToDo: Implement other parameters
                    if (p[4] == 0 && p[5] != 0 && (collisionFlags & CollisionFlags.CollideWithTileset) != 0) {
                        break;
                    }

                    EndDamagingMove();

                    SetAnimation(AnimState.Dash | AnimState.Jump);

                    controllable = false;
                    canJump = false;
                    collisionFlags &= ~CollisionFlags.ApplyGravitation;

                    speedX = unchecked((short)p[0]);
                    speedY = unchecked((short)p[1]);

                    if (speedX == 0f) {
                        pos.X = (MathF.Floor(pos.X / 32) * 32) + 16;
                        Transform.Pos = pos;
                        OnUpdateHitbox();
                    } else if (speedY == 0f) {
                        pos.Y = (MathF.Floor(pos.Y / 32) * 32) + 8;
                        Transform.Pos = pos;
                        OnUpdateHitbox();
                    } else if (inTubeTime <= 0f) {
                        pos.X = (MathF.Floor(pos.X / 32) * 32) + 16;
                        pos.Y = (MathF.Floor(pos.Y / 32) * 32) + 8;
                        Transform.Pos = pos;
                        OnUpdateHitbox();
                    }

                    if (p[4] != 0) { // Become Noclip
                        collisionFlags &= ~CollisionFlags.CollideWithTileset;
                        inTubeTime = 60f;
                    } else {
                        inTubeTime = 10f;
                    }
                    break;
                }
                case EventType.AreaEndOfLevel: { // ExitType, Fast (No score count, only black screen), TextID, TextOffset, Coins
                    if (levelExiting == LevelExitingState.None) {
                        // ToDo: Implement Fast parameter
                        if (p[4] <= coins) {
                            coins -= p[4];

                            string nextLevel;
                            if (p[2] == 0) {
                                nextLevel = null;
                            } else {
                                nextLevel = api.GetLevelText(p[2]).SubstringByOffset('|', p[3]);
                            }
                            api.InitLevelChange((ExitType)p[0], nextLevel);
                            PlaySound("EndOfLevel");
                        } else if (bonusWarpTimer <= 0f) {
                            attachedHud?.ShowCoins(coins);
                            PlaySound("BonusWarpNotEnoughCoins");

                            bonusWarpTimer = 400f;
                        }
                    }
                    break;
                }
                case EventType.AreaText: { // Text, TextOffset, Vanish
                    string text = api.GetLevelText(p[0]);
                    if (p[1] != 0) {
                        text = text.SubstringByOffset('|', p[1]);
                    }
                    if (!string.IsNullOrEmpty(text)) {
                        attachedHud?.ShowLevelText(text);
                    }
                    if (p[2] != 0) {
                        api.EventMap.StoreTileEvent((int)(pos.X / 32), (int)(pos.Y / 32), EventType.Empty);
                    }
                    break;
                }
                case EventType.AreaCallback: { // Function, Param, Vanish
                    // ToDo: Call function #{p[0]}(sender, p[1]); implement level extensions
                    attachedHud?.ShowLevelText("\f[s:75]\f[w:95]\f[c:6]\n\n\n\nWARNING: Callbacks aren't implemented yet. (" + p[0] + ", " + p[1] + ")");
                    if (p[2] != 0) {
                        api.EventMap.StoreTileEvent((int)(pos.X / 32), (int)(pos.Y / 32), EventType.Empty);
                    }
                    break;
                }
                case EventType.AreaActivateBoss: { // Music
                    api.BroadcastTriggeredEvent(tileEvent, p);

                    // Deactivate sugar rush if it's active
                    if (sugarRushLeft > 1f) {
                        sugarRushLeft = 1f;
                    }
                    break;
                }
                case EventType.AreaFlyOff: {
                    if (activeModifier == Modifier.Airboard) {
                        SetModifier(Modifier.None);
                    }
                    break;
                }
                case EventType.AreaRevertMorph: {
                    if (playerType != playerTypeOriginal) {
                        MorphRevent();
                    }
                    break;
                }
                case EventType.AreaMorphToFrog: {
                    if (playerType != PlayerType.Frog) {
                        MorphTo(PlayerType.Frog);
                    }
                    break;
                }
                case EventType.AreaNoFire: {
                    switch (p[0]) {
                        case 0: areaWeaponAllowed = false; break;
                        case 1: weaponAllowed = true; break;
                        case 2: weaponAllowed = false; break;
                    }
                    break;
                }
                case EventType.TriggerZone: { // Trigger ID, Turn On, Switch
                    TileMap tiles = api.TileMap;
                    if (tiles != null) {
                        // ToDo: Implement Switch parameter
                        tiles.SetTrigger(p[0], p[1] != 0);
                    }
                    break;
                }

                case EventType.ModifierDeath: {
                    DecreaseHealth(int.MaxValue);
                    break;
                }
                case EventType.ModifierSetWater: { // Height, Instant, Lighting
                    // ToDo: Implement Instant (non-instant transition), Lighting
                    api.WaterLevel = p[0];
                    break;
                }
                case EventType.ModifierLimitCameraView: { // Left, Width
                    api.LimitCameraView((p[0] == 0 ? (int)(pos.X / 32) : p[0]) * 32, p[1] * 32);
                    break;
                }

                case EventType.RollingRockTrigger: { // Rock ID
                    api.BroadcastTriggeredEvent(tileEvent, p);
                    break;
                }

                case EventType.AreaWaterBlock: {
                    areaWaterBlock = ((int)pos.Y / 32) * 32 + p[0];
                    break;
                }
            }

            // ToDo: Implement Slide modifier with JJ2+ parameter

            // Check floating from each corner of an extended hitbox
            // Player should not pass from a single tile wide gap if the columns left or right have
            // float events, so checking for a wider box is necessary.
            const float extendedHitbox = 2f;

            if (currentSpecialMove != SpecialMoveType.Buttstomp) {
                if ((events.GetEventByPosition(pos.X, pos.Y, ref p) == EventType.AreaFloatUp) ||
                    (events.GetEventByPosition(AABBInner.LowerBound.X - extendedHitbox, AABBInner.LowerBound.Y - extendedHitbox, ref p) == EventType.AreaFloatUp) ||
                    (events.GetEventByPosition(AABBInner.UpperBound.X + extendedHitbox, AABBInner.LowerBound.Y - extendedHitbox, ref p) == EventType.AreaFloatUp) ||
                    (events.GetEventByPosition(AABBInner.UpperBound.X + extendedHitbox, AABBInner.UpperBound.Y + extendedHitbox, ref p) == EventType.AreaFloatUp) ||
                    (events.GetEventByPosition(AABBInner.LowerBound.X - extendedHitbox, AABBInner.UpperBound.Y + extendedHitbox, ref p) == EventType.AreaFloatUp)
                ) {
                    if ((collisionFlags & CollisionFlags.ApplyGravitation) != 0) {
                        float gravity = api.Gravity;

                        externalForceY = gravity * 2f * timeMult;
                        speedY = MathF.Min(gravity * timeMult, speedY);
                    } else {
                        speedY -= api.Gravity * 1.2f * timeMult;
                    }
                }
            }

            if ((events.GetEventByPosition(pos.X, pos.Y, ref p) == EventType.AreaHForce) ||
                (events.GetEventByPosition(AABBInner.LowerBound.X - extendedHitbox, AABBInner.LowerBound.Y - extendedHitbox, ref p) == EventType.AreaHForce) ||
                (events.GetEventByPosition(AABBInner.UpperBound.X + extendedHitbox, AABBInner.LowerBound.Y - extendedHitbox, ref p) == EventType.AreaHForce) ||
                (events.GetEventByPosition(AABBInner.UpperBound.X + extendedHitbox, AABBInner.UpperBound.Y + extendedHitbox, ref p) == EventType.AreaHForce) ||
                (events.GetEventByPosition(AABBInner.LowerBound.X - extendedHitbox, AABBInner.UpperBound.Y + extendedHitbox, ref p) == EventType.AreaHForce)
               ) {
                if ((p[5] != 0 || p[4] != 0)) {
                    MoveInstantly(new Vector2((p[5] - p[4]) * 0.4f * timeMult, 0), MoveType.Relative);
                }
            }

            //
            if (canJump) {
                // Floor events
                tileEvent = events.GetEventByPosition(pos.X, pos.Y + 32, ref p);
                switch (tileEvent) {
                    case EventType.AreaHForce: {
                        if (p[1] != 0 || p[0] != 0) {
                            MoveInstantly(new Vector2((p[1] - p[0]) * 0.4f * timeMult, 0), MoveType.Relative);
                        }
                        if (p[3] != 0 || p[2] != 0) {
                            speedX += (p[3] - p[2]) * 0.1f;
                        }
                        break;
                    }
                }
            }
        }

        public override void OnHandleCollision(ActorBase other)
        {
            //base.OnHandleCollision(other);

            bool removeSpecialMove = false;

            switch (other) {
                case TurtleShell collider: {
                    if (currentSpecialMove != SpecialMoveType.None || sugarRushLeft > 0f) {
                        collider.DecreaseHealth(int.MaxValue, this);

                        if ((currentAnimationState & AnimState.Buttstomp) != 0) {
                            removeSpecialMove = true;
                            speedY *= -0.6f;
                            canJump = true;
                        }
                    }
                    break;
                }

                case EnemyBase collider: {
                    if (currentSpecialMove != SpecialMoveType.None || sugarRushLeft > 0f || shieldTime > 0f) {
                        if (!collider.IsInvulnerable) {
                            collider.DecreaseHealth(4, this);

                            Explosion.Create(api, collider.Transform.Pos, Explosion.Small);

                            if (sugarRushLeft > 0f) {
                                if (canJump) {
                                    speedY = 3;
                                    canJump = false;
                                    externalForceY = 0.6f;
                                }
                                speedY *= -0.5f;
                            }
                            if ((currentAnimationState & AnimState.Buttstomp) != 0) {
                                removeSpecialMove = true;
                                speedY *= -0.6f;
                                canJump = true;
                            } else if (currentSpecialMove != SpecialMoveType.None && collider.Health >= 0) {
                                removeSpecialMove = true;
                                externalForceX = 0f;
                                externalForceY = 0f;
                            }
                        }

                        // Decrease remaining shield time by 5 secs
                        if (shieldTime > 0f) {
                            shieldTime = Math.Max(1f, shieldTime - 5f * Time.FramesPerSecond);
                        }
                    } else if (collider.CanHurtPlayer) {
                        TakeDamage(1, 4 * (Transform.Pos.X > collider.Transform.Pos.X ? 1 : -1));
                    }
                    break;
                }

                case Spring spring: {
                    // Collide only with hitbox
                    if (AABB.TestOverlap(ref spring.AABBInner, ref AABBInner)) {
                        Vector2 force = spring.Activate();
                        int sign = ((force.X + force.Y) > float.Epsilon ? 1 : -1);
                        if (MathF.Abs(force.X) > 0f) {
                            removeSpecialMove = true;
                            copterFramesLeft = 0f;
                            //speedX = force.X;
                            speedX = (1 + MathF.Abs(force.X)) * sign;
                            externalForceX = force.X * 0.6f;

                            wasActivelyPushing = false;

                            keepRunningTime = 100f;

                            if (!spring.KeepSpeedY) {
                                speedY = 0f;
                                externalForceY = 0f;
                            }

                            SetPlayerTransition(AnimState.Dash | AnimState.Jump, true, true, SpecialMoveType.None);
                            // ToDo: ...
                            controllableTimeout = 20f;
                        } else if (MathF.Abs(force.Y) > 0f) {
                            copterFramesLeft = 0f;
                            speedY = (4 + MathF.Abs(force.Y)) * sign;
                            externalForceY = -force.Y;

                            if (!spring.KeepSpeedX) {
                                speedX = 0f;
                                externalForceX = 0f;
                                keepRunningTime = 0f;
                            }

                            if (sign > 0) {
                                removeSpecialMove = false;
                                currentSpecialMove = SpecialMoveType.Buttstomp;
                                SetAnimation(AnimState.Buttstomp);
                            } else {
                                removeSpecialMove = true;
                                isSpring = true;
                            }

                            PlaySound("Spring");
                        } else {
                            break;
                        }
                        canJump = false;
                    }
                    break;
                }

                case PinballBumper bumper: {
                    Vector2 force = bumper.Activate(this);
                    if (force != Vector2.Zero) {
                        removeSpecialMove = true;
                        canJump = false;

                        speedX += force.X * 0.4f;
                        speedY += force.Y * 0.4f;
                        externalForceX += force.X * 0.04f;
                        externalForceY -= force.Y * 0.04f;

                        // ToDo: Check this...
                        AddScore(500);
                    }
                    break;
                }

                case PinballPaddle paddle: {
                    Vector2 force = paddle.Activate(this);
                    if (force != Vector2.Zero) {
                        removeSpecialMove = true;
                        copterFramesLeft = 0f;
                        canJump = false;

                        speedX = force.X;
                        speedY = force.Y;
                    }
                    break;
                }

                case SwingingVine vine: {
                    if (currentVine == null && speedY > 1f) {
                        currentVine = vine;
                        suspendType = SuspendType.SwingingVine;
                        collisionFlags &= ~CollisionFlags.ApplyGravitation;
                    }
                    break;
                }

                case BonusWarp warp: {
                    if (currentTransitionState == AnimState.Idle || currentTransitionCancellable) {
                        if (warp.Cost <= coins) {
                            coins -= warp.Cost;
                            warp.Activate(this);

                            // Convert remaing coins to gems
                            gems += coins;
                            coins = 0;
                        } else if (bonusWarpTimer <= 0f) {
                            attachedHud?.ShowCoins(coins);
                            PlaySound("BonusWarpNotEnoughCoins");

                            bonusWarpTimer = 400f;
                        }
                    }
                    break;
                }
            }

            if (removeSpecialMove) {
                controllable = true;
                EndDamagingMove();
            }
        }

        private void EndDamagingMove()
        {
            collisionFlags |= CollisionFlags.ApplyGravitation;
            SetAnimation(currentAnimationState & ~(AnimState.Uppercut | AnimState.Buttstomp));

            if (currentSpecialMove == SpecialMoveType.Uppercut) {
                if (suspendType == SuspendType.None) {
                    SetTransition(AnimState.TransitionUppercutEnd, false);
                }
                controllable = true;

                if (externalForceY > 0f) {
                    externalForceY = 0f;
                }
            }

            currentSpecialMove = SpecialMoveType.None;
        }

        private bool SetPlayerTransition(AnimState state, bool cancellable, bool removeControl, SpecialMoveType specialMove, Action callback = null)
        {
            if (removeControl) {
                controllable = false;
                controllableTimeout = 0f;
            }

            currentSpecialMove = specialMove;
            return SetTransition(state, cancellable, callback);
        }

        public void TakeDamage(int amount, float pushForce)
        {
            if (!isInvulnerable && levelExiting == LevelExitingState.None) {
                // Cancel active climbing
                if (currentTransitionState == AnimState.TransitionLedgeClimb) {
                    ForceCancelTransition();

                    MoveInstantly(new Vector2(IsFacingLeft ? 6f : -6f, 0f), MoveType.Relative, true);
                }

                DecreaseHealth(amount, null);

                internalForceY = 0f;
                speedX = 0f;
                canJump = false;
                isAttachedToPole = false;

                fireFramesLeft = copterFramesLeft = pushFramesLeft = 0f;

                if (activeBird != null) {
                    activeBird.FlyAway();
                    activeBird = null;
                }

                if (health > 0) {
                    externalForceX = pushForce;

                    if (!inWater && activeModifier == Modifier.None) {
                        speedY = -6.5f;

                        collisionFlags |= CollisionFlags.ApplyGravitation;
                        SetAnimation(AnimState.Idle);
                    } else {
                        speedY = -1f;
                    }

                    SetPlayerTransition(AnimState.Hurt, false, true, SpecialMoveType.None, delegate {
                        controllable = true;
                    });
                    SetInvulnerability(180f, false);

                    PlaySound("Hurt");
                } else {
                    externalForceX = 0f;
                    speedY = 0f;

                    PlaySound("Die", 1.3f);
                }
            }
        }

        public void Respawn(Vector2? pos = null)
        {
            if (health > 0) {
                return;
            }

            // Reset health
            health = maxHealth;

            if (pos == null) {
                // Return to the last save point
                MoveInstantly(checkpointPos, MoveType.Absolute, true);
                api.AmbientLight = checkpointLight;
                api.LimitCameraView(0, 0);
                api.WarpCameraToTarget(this);
            } else {
                MoveInstantly(pos.Value, MoveType.Absolute, true);
                api.WarpCameraToTarget(this);
            }

            isInvulnerable = false;
            collisionFlags |= CollisionFlags.ApplyGravitation;

            controllable = true;
            renderer.Active = true;
        }

        public void WarpToPosition(Vector2 pos, bool fast)
        {
            if (fast) {
                Vector3 posOld = Transform.Pos;

                MoveInstantly(pos, MoveType.Absolute, true);

                if (new Vector2(posOld.X - pos.X, posOld.Y - pos.Y).Length > 250) {
                    api.WarpCameraToTarget(this);
                }
            } else {
                EndDamagingMove();
                isInvulnerable = true;
                collisionFlags &= ~CollisionFlags.ApplyGravitation;

                SetAnimation(currentAnimationState & ~(AnimState.Uppercut | AnimState.Buttstomp));

                speedX = 0f;
                speedY = 0f;
                externalForceX = 0f;
                externalForceY = 0f;
                internalForceY = 0f;
                fireFramesLeft = 0f;
                copterFramesLeft = 0f;
                pushFramesLeft = 0f;

                // For warping from the water
                Transform.Angle = 0f;

                PlaySound("WarpIn");

                SetPlayerTransition(isFreefall ? AnimState.TransitionWarpInFreefall : AnimState.TransitionWarpIn, false, true, SpecialMoveType.None, delegate {
                    Vector3 posOld = Transform.Pos;

                    MoveInstantly(pos, MoveType.Absolute, true);
                    PlaySound("WarpOut");

                    if (new Vector2(posOld.X - pos.X, posOld.Y - pos.Y).Length > 250) {
                        api.WarpCameraToTarget(this);
                    }

                    isFreefall = isFreefall || CanFreefall();
                    SetPlayerTransition(isFreefall ? AnimState.TransitionWarpOutFreefall : AnimState.TransitionWarpOut, false, true, SpecialMoveType.None, delegate {
                        isInvulnerable = false;
                        collisionFlags |= CollisionFlags.ApplyGravitation;
                        controllable = true;
                    });
                });
            }
        }

        private void InitialPoleStage(bool horizontal)
        {
            if (isAttachedToPole || playerType == PlayerType.Frog) {
                return;
            }

            Vector3 pos = Transform.Pos;
            int x = (int)pos.X / 32;
            int y = (int)pos.Y / 32;

            if (lastPoleTime > 0f && lastPolePos.X == x && lastPolePos.Y == y) {
                return;
            }

            lastPoleTime = 80f;
            lastPolePos = new Point2(x, y);

            float activeForce, lastSpeed;
            if (horizontal) {
                activeForce = (Math.Abs(externalForceX) > 1f ? externalForceX : speedX);
                lastSpeed = speedX;
            } else {
                activeForce = speedY;
                lastSpeed = speedY;
            }
            bool positive = (activeForce >= 0);

            pos.X = x * 32 + 16;
            pos.Y = y * 32 + 16;
            Transform.Pos = pos;

            OnUpdateHitbox();

            speedX = 0;
            speedY = 0;
            externalForceX = 0;
            externalForceY = 0;
            internalForceY = 0;
            collisionFlags &= ~CollisionFlags.ApplyGravitation;
            isAttachedToPole = true;
            inIdleTransition = false;

            keepRunningTime = 0f;

            SetAnimation(currentAnimationState & ~(AnimState.Uppercut /*| AnimState.Sidekick*/ | AnimState.Buttstomp));

            AnimState poleAnim = (horizontal ? AnimState.TransitionPoleHSlow : AnimState.TransitionPoleVSlow);
            SetPlayerTransition(poleAnim, false, true, SpecialMoveType.None, delegate {
                NextPoleStage(horizontal, positive, 2, lastSpeed);
            });

            controllableTimeout = 80f;

            PlaySound("Pole", 0.8f, 0.6f);
        }

        private void NextPoleStage(bool horizontal, bool positive, int stagesLeft, float lastSpeed)
        {
            if (stagesLeft > 0) {
                AnimState poleAnim = (horizontal ? AnimState.TransitionPoleH : AnimState.TransitionPoleV);
                SetPlayerTransition(poleAnim, false, true, SpecialMoveType.None, delegate {
                    NextPoleStage(horizontal, positive, stagesLeft - 1, lastSpeed);
                });

                inIdleTransition = false;
                controllableTimeout = 80f;

                PlaySound("Pole", 1f, 0.6f);
            } else {
                int sign = (positive ? 1 : -1);
                if (horizontal) {
                    // To prevent stucking
                    for (int i = -1; i > -6; i--) {
                        if (MoveInstantly(new Vector2(speedX, i), MoveType.Relative)) {
                            break;
                        }
                    }

                    speedX = 10 * sign + lastSpeed * 0.2f;
                    externalForceX = 10 * sign;
                    IsFacingLeft = !positive;

                    keepRunningTime = 60f;

                    SetPlayerTransition(AnimState.Dash | AnimState.Jump, true, true, SpecialMoveType.None);
                } else {
                    MoveInstantly(new Vector2(0, sign * 16), MoveType.Relative, true);

                    speedY = 4 * sign + lastSpeed * 1.4f;
                    externalForceY = (-1.3f * sign);
                }

                collisionFlags |= CollisionFlags.ApplyGravitation;
                isAttachedToPole = false;
                wasActivelyPushing = false;
                inIdleTransition = false;

                controllableTimeout = 4f;
                lastPoleTime = 10f;

                PlaySound("HookAttach", 0.8f, 1.2f);
            }
        }

        private bool CanFreefall()
        {
            Vector3 pos = Transform.Pos;
            AABB aabb = new AABB(pos.X - 14, pos.Y + 8 - 12, pos.X + 14, pos.Y + 8 + 12 + 100);
            return api.IsPositionEmpty(this, ref aabb, true);
        }

        public void SetCarryingPlatform(MovingPlatform platform)
        {
            if (speedY < 0f || inWater || activeModifier != Modifier.None) {
                return;
            }

            carryingObject = platform;
            canJump = true;
            internalForceY = 0;
            speedY = 0;
        }

        private void FollowCarryingPlatform()
        {
            if (carryingObject != null) {
                if (!canJump || !controllable || (collisionFlags & CollisionFlags.ApplyGravitation) == 0) {
                    carryingObject = null;
                } else {
                    Vector2 delta = carryingObject.GetLocationDelta();

                    // Try to adjust Y, because it collides with carrying platform sometimes
                    for (int i = 0; i < 4; i++) {
                        delta.Y -= 1f;
                        if (MoveInstantly(delta, MoveType.Relative)) {
                            return;
                        }
                    }

                    MoveInstantly(new Vector2(0f, delta.Y), MoveType.Relative);
                }
            } else if (currentVine != null) {
                Vector3 pos = Transform.Pos;
                Vector2 newPos = currentVine.AttachPoint + new Vector2(0f, 14f);
                MoveInstantly(newPos, MoveType.Absolute);

                if (IsFacingLeft) {
                    if (newPos.X > pos.X) {
                        renderer.AnimTime = 0;
                        IsFacingLeft = false;
                    }
                } else {
                    if (newPos.X < pos.X) {
                        renderer.AnimTime = 0;
                        IsFacingLeft = true;
                    }
                }
            }
        }

        public void SetInvulnerability(float time, bool withCircleEffect)
        {
            if (time <= 0f) {
                isInvulnerable = false;
                invulnerableTime = 0;

                SetCircleEffect(false);
                return;
            }

            isInvulnerable = true;
            invulnerableTime = time;

            if (withCircleEffect) {
                SetCircleEffect(true);
            }
        }

        public void AttachToHud(Hud hud)
        {
            if (attachedHud != null) {
                attachedHud.Owner = null;
            }

            attachedHud = hud;

            if (attachedHud != null) {
                attachedHud.Owner = this;
            }
        }
    }
}