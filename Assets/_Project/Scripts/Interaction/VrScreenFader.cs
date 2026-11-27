using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace CafeGame.Interaction
{
    // Short, deterministic fade-to-black / fade-from-black for the XR camera, used to
    // hide the pop of a scripted relocation (VrOrderRelocator). Drives a single
    // screen-space-camera Image's alpha; does not touch the camera or XR Origin itself.
    public sealed class VrScreenFader : MonoBehaviour
    {
        [SerializeField] private Image fadeImage;

        private void Awake()
        {
            SetAlpha(0f);
        }

        public async Task FadeToBlackAsync(float seconds)
        {
            await FadeAsync(0f, 1f, seconds);
        }

        public async Task FadeFromBlackAsync(float seconds)
        {
            await FadeAsync(1f, 0f, seconds);
        }

        private async Task FadeAsync(float from, float to, float seconds)
        {
            if (fadeImage == null)
            {
                return;
            }

            if (seconds <= 0f)
            {
                SetAlpha(to);
                return;
            }

            float elapsed = 0f;
            SetAlpha(from);

            while (elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / seconds);
                SetAlpha(Mathf.Lerp(from, to, t));
                await Task.Yield();
            }

            SetAlpha(to);
        }

        private void SetAlpha(float alpha)
        {
            if (fadeImage == null)
            {
                return;
            }

            Color color = fadeImage.color;
            color.a = alpha;
            fadeImage.color = color;
        }
    }
}
