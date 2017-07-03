﻿using System;
using Jazz2.Game.Structs;

namespace Jazz2.Actors.Enemies
{
    public class Sucker : EnemyBase
    {
        private int cycle;

        public override void OnAttach(ActorInstantiationDetails details)
        {
            base.OnAttach(details);

            LastHitDirection parentLastHitDir = (LastHitDirection)details.Params[0];

            SetHealthByDifficulty(1);
            scoreValue = 100;

            RequestMetadata("Enemy/Sucker");
            maxHealth = 4;
            SetAnimation(AnimState.Walk);

            if (parentLastHitDir != LastHitDirection.None) {
                isFacingLeft = (parentLastHitDir == LastHitDirection.Left);
                health = 1;
                collisionFlags &= ~CollisionFlags.ApplyGravitation;
                SetTransition((AnimState)1073741824, false, delegate {
                    speedX = 0;
                    SetAnimation(AnimState.Walk);
                    collisionFlags |= CollisionFlags.ApplyGravitation;
                });
                if (parentLastHitDir == LastHitDirection.Left || parentLastHitDir == LastHitDirection.Right) {
                    speedX = 3 * (parentLastHitDir == LastHitDirection.Left ? -1 : 1);
                }
                PlaySound("ENEMY_SUCKER_DEFLATE");
            } else {
                health = 4;
            }
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            if (frozenTimeLeft > 0) {
                return;
            }

            if (currentTransitionState == AnimState.Idle && Math.Abs(speedY) < float.Epsilon && Math.Abs(speedX) > float.Epsilon && !CanMoveToPosition(speedX, 0)) {
                isFacingLeft = !isFacingLeft;
                speedX *= -1;
            }

            if (currentTransitionState == AnimState.Idle && frozenTimeLeft <= 0) {
                cycle = (cycle + 1) % (7 * 12);
                if (cycle == 0) {
                    PlaySound("ENEMY_SUCKER_WALK_1", 0.4f);
                }
                if (cycle == (7 * 7)) {
                    PlaySound("ENEMY_SUCKER_WALK_2", 0.4f);
                }
                if (cycle == (3 * 7) || cycle == (8 * 7)) {
                    PlaySound("ENEMY_SUCKER_WALK_3", 0.4f);
                }
                if ((cycle >= (6 * 7) && cycle < (8 * 7)) || cycle >= (10 * 7)) {
                    speedX = 0.5f * (isFacingLeft ? -1 : 1);
                } else {
                    speedX = 0;
                }
            }
        }

        protected override bool OnPerish(ActorBase collider)
        {
            CreateParticleDebris();
            api.PlayCommonSound(this, "COMMON_SPLAT");

            TryGenerateRandomDrop();

            return base.OnPerish(collider);
        }
    }
}