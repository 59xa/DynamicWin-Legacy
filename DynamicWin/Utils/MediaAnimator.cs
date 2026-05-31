using SkiaSharp;
using System;

namespace DynamicWin.Utils
{
    /// <summary>
    /// Encapsulates animation state machine used for media thumbnail flip/blur animations.
    /// The owner is responsible for providing swap/cleanup actions via callbacks so bitmaps/metadata
    /// remain under owner's control and locking semantics.
    /// </summary>
    public class MediaAnimator
    {
        public enum AnimState { Idle, BlurIn, Flip, BlurOut }

        public AnimState State { get; private set; } = AnimState.Idle;
        public float AnimTimer { get; private set; } = 0f;
        public float BlurAmount { get; private set; } = 0f;

        private readonly float blurDur;
        private readonly float flipDur;
        private readonly float blurOutDur;
        private readonly float maxBlur;

        private bool midSwapCalled = false;
        // Track previous hasPending value so we only start when a pending appears (rising edge)
        private bool lastHasPending = false;

        public MediaAnimator(float blurDur = 0.18f, float flipDur = 0.36f, float blurOutDur = 0.18f, float maxBlur = 8f)
        {
            this.blurDur = blurDur;
            this.flipDur = flipDur;
            this.blurOutDur = blurOutDur;
            this.maxBlur = maxBlur;
        }

        /// <summary>
        /// Step the animator.
        /// </summary>
        /// <remarks>
        /// - hasPending is evaluated to decide when to start the animation or when to perform the mid-flip swap.
        /// - onStart is invoked when the animator transitions from Idle -> BlurIn (owner should capture previous bitmap here).
        /// - onMidFlip is invoked once when the flip progress reaches the halfway point and there is a pending bitmap to swap.
        /// - onFinish is invoked when the animation finishes (Idle again) to allow owner cleanup e.g. disposing previous bitmap.
        /// </remarks>
        public void Update(float deltaTime, Func<bool> hasPending, Action? onStart = null, Action? onMidFlip = null, Action? onFinish = null)
        {
            bool hasPendingNow = false;
            try { hasPendingNow = hasPending(); } catch { hasPendingNow = false; }
            bool resetPendingEdge = false;

            if (State != AnimState.Idle)
                AnimTimer += deltaTime;

            if (State == AnimState.BlurIn)
            {
                float t = Math.Min(1f, AnimTimer / blurDur);
                float e = Easings.EaseInOutCubic(t);
                BlurAmount = Mathf.Lerp(0f, maxBlur, e);
                if (t >= 1f)
                {
                    State = AnimState.Flip;
                    AnimTimer = 0f;
                    midSwapCalled = false;
                }
            }
            else if (State == AnimState.Flip)
            {
                float t = Math.Min(1f, AnimTimer / flipDur);
                float e = Easings.EaseInOutCubic(t);

                if (e >= 0.5f && !midSwapCalled && hasPendingNow)
                {
                    midSwapCalled = true;
                    try { onMidFlip?.Invoke(); } catch { }
                }

                if (t >= 1f)
                {
                    State = AnimState.BlurOut;
                    AnimTimer = 0f;
                }
            }
            else if (State == AnimState.BlurOut)
            {
                float t = Math.Min(1f, AnimTimer / blurOutDur);
                float e = Easings.EaseInOutCubic(t);
                BlurAmount = Mathf.Lerp(maxBlur, 0f, e);
                if (t >= 1f)
                {
                    BlurAmount = 0f;
                    State = AnimState.Idle;
                    AnimTimer = 0f;
                    try { onFinish?.Invoke(); } catch { }
                    resetPendingEdge = true;
                }
            }
            else // Idle
            {
                // Only start animation on a rising edge (no repeated starts while pending stays true).
                if (hasPendingNow && !lastHasPending)
                {
                    // Transition to BlurIn
                    try { onStart?.Invoke(); } catch { }
                    State = AnimState.BlurIn;
                    AnimTimer = 0f;
                }
            }

            // Update lastHasPending for edge detection on next frame. When animation is running we still track
            // the pending state so mid-swap logic can use hasPendingNow above.
            lastHasPending = resetPendingEdge ? false : hasPendingNow;
        }

        /// <summary>
        /// Returns the current horizontal flip scale for drawing (cosine eased). Returns 1 when not flipping.
        /// </summary>
        public float GetFlipScale()
        {
            if (State != AnimState.Flip) return 1f;
            float t = Math.Min(1f, AnimTimer / flipDur);
            float e = Easings.EaseInOutCubic(t);
            return (float)Math.Cos(e * Math.PI);
        }

        public bool IsFlipping => State == AnimState.Flip;

        /// <summary>
        /// Forces the animation to finish immediately, resetting state and blur.
        /// </summary>
        public void ForceFinish()
        {
            State = AnimState.Idle;
            AnimTimer = 0f;
            BlurAmount = 0f;
            midSwapCalled = false;
            lastHasPending = false;
        }
    }
}
