using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace CafeGame.Interaction
{
    [RequireComponent(typeof(XRSimpleInteractable))]
    public sealed class VrInteractionProbe : MonoBehaviour
    {
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [SerializeField] private Color idleColor = new Color(0.05f, 0.75f, 1f);
        [SerializeField] private Color selectedColor = new Color(0.15f, 1f, 0.25f);

        private XRSimpleInteractable interactable;
        private Renderer targetRenderer;
        private MaterialPropertyBlock propertyBlock;

        private void Awake()
        {
            interactable = GetComponent<XRSimpleInteractable>();
            targetRenderer = GetComponent<Renderer>();
            propertyBlock = new MaterialPropertyBlock();
        }

        private void OnEnable()
        {
            interactable.selectEntered.AddListener(HandleSelected);
            interactable.selectExited.AddListener(HandleDeselected);
            SetColor(idleColor);
        }

        private void OnDisable()
        {
            interactable.selectEntered.RemoveListener(HandleSelected);
            interactable.selectExited.RemoveListener(HandleDeselected);
        }

        private void HandleSelected(SelectEnterEventArgs args)
        {
            SetColor(selectedColor);
            Debug.Log($"[VR Probe] Selected by {args.interactorObject.transform.name}.", this);
        }

        private void HandleDeselected(SelectExitEventArgs args)
        {
            SetColor(idleColor);
            Debug.Log("[VR Probe] Selection released.", this);
        }

        private void SetColor(Color color)
        {
            if (targetRenderer == null)
            {
                return;
            }

            targetRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor(BaseColor, color);
            targetRenderer.SetPropertyBlock(propertyBlock);
        }
    }
}
