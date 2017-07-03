﻿using System;
using System.Collections.Generic;
using Duality;
using Duality.Drawing;
using Duality.Input;

namespace Jazz2.Game.Menu
{
    public class InGameMenuBeginSection : InGameMenuSection
    {
        private List<Tuple<string, Action>> items;

        private int selectedIndex;
        private float animation;

        public InGameMenuBeginSection()
        {
            items = new List<Tuple<string, Action>> {
                Tuple.Create<string, Action>("Resume", OnPlayStoryPressed),
                Tuple.Create<string, Action>("Exit to menu", OnExitPressed),
            };
        }

        public override void OnShow(InGameMenu root)
        {
            animation = 0f;
            base.OnShow(root);
        }

        public override void OnPaint(IDrawDevice device, Canvas c)
        {
            Vector2 center = device.TargetSize * 0.5f;
            center.Y *= 0.96f;

            const float topLine = 131f;
            float bottomLine = device.TargetSize.Y - 42;
            api.DrawMaterial(c, "MenuDim", center.X, (topLine + bottomLine) * 0.5f, Alignment.Center, new ColorRgba(0f, 1f), 80f, (bottomLine - topLine) / 7.6f);

            int charOffset = 0;
            for (int i = 0; i < items.Count; i++) {
                if (selectedIndex == i) {
                    float size = 0.5f + MainMenu.EaseOutElastic(animation) * 0.6f;

                    api.DrawStringShadow(device, ref charOffset, items[i].Item1, center.X, center.Y,
                        Alignment.Center, null, size, 0.7f, 1.1f, 1.1f, charSpacing: 0.9f);
                } else {
                    api.DrawString(device, ref charOffset, items[i].Item1, center.X, center.Y,
                        Alignment.Center, ColorRgba.TransparentBlack, 0.9f);
                }

                center.Y += 34f + 8f;
            }
        }

        public override void OnUpdate()
        {
            if (animation < 1f) {
                animation = Math.Min(animation + Time.TimeMult * 0.016f, 1f);
            }

            if (DualityApp.Keyboard.KeyHit(Key.Enter)) {
                api.PlaySound("MenuSelect", 0.5f);
                items[selectedIndex].Item2();
            } else if (DualityApp.Keyboard.KeyHit(Key.Escape)) {
                api.PlaySound("MenuSelect", 0.5f);
                api.SwitchToCurrentGame();
            } else if (DualityApp.Keyboard.KeyHit(Key.Up)) {
                api.PlaySound("MenuSelect", 0.4f);
                animation = 0f;
                if (selectedIndex > 0) {
                    selectedIndex--;
                } else {
                    selectedIndex = items.Count - 1;
                }
            } else if (DualityApp.Keyboard.KeyHit(Key.Down)) {
                api.PlaySound("MenuSelect", 0.4f);
                animation = 0f;
                if (selectedIndex < items.Count - 1) {
                    selectedIndex++;
                } else {
                    selectedIndex = 0;
                }
            }
        }

        private void OnPlayStoryPressed()
        {
            api.SwitchToCurrentGame();
        }

        private void OnExitPressed()
        {
            api.SwitchToMainMenu();
        }
    }
}