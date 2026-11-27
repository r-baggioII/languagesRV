using UnityEngine;

// Opens the mouth surface in response to the actual TTS clip samples.
[RequireComponent(typeof(RemyVoiceController), typeof(AudioSource))]
public sealed class VrRemySpeechFace : MonoBehaviour
{
    private const int SampleFrames = 512;
    private const int EllipseSegments = 24;

    [SerializeField] private Material lipMaterial;
    [SerializeField] private Material mouthMaterial;
    [SerializeField] private Transform headAnchor;
    [SerializeField] private Vector3 mouthPositionOnHead = new Vector3(0f, 0.052f, 0.155f);
    [SerializeField, Range(0.1f, 30f)] private float audioSensitivity = 10f;

    private RemyVoiceController voice;
    private AudioSource speechAudio;
    private Transform mouthRoot;
    private Mesh mouthMesh;
    private AudioClip sampledClip;
    private float[] samples;
    private float opening;

    private void Awake()
    {
        voice = GetComponent<RemyVoiceController>();
        speechAudio = GetComponent<AudioSource>();

        Transform head = headAnchor;
        if (head == null && TryGetComponent(out Animator animator) && animator.isHuman)
        {
            head = animator.GetBoneTransform(HumanBodyBones.Head);
        }

        if (head == null || lipMaterial == null || mouthMaterial == null)
        {
            Debug.LogWarning("[VrRemySpeechFace] Head or mouth materials are missing.", this);
            enabled = false;
            return;
        }

        mouthMesh = CreateEllipse();
        var root = new GameObject("Speech Mouth");
        mouthRoot = root.transform;
        mouthRoot.SetParent(head, false);
        mouthRoot.localPosition = mouthPositionOnHead;
        CreateSurface("Lip Rim", lipMaterial, new Vector3(0.044f, 0.021f, 1f), 0f);
        CreateSurface("Mouth Opening", mouthMaterial, new Vector3(0.035f, 0.014f, 1f), 0.002f);
        mouthRoot.gameObject.SetActive(false);
    }

    private void LateUpdate()
    {
        if (mouthRoot == null)
        {
            return;
        }

        float target = voice != null && voice.IsSpeaking ? ReadAudioLevel() : 0f;
        opening = Mathf.Lerp(opening, target, 1f - Mathf.Exp(-20f * Time.deltaTime));

        bool visible = opening > 0.04f;
        if (mouthRoot.gameObject.activeSelf != visible)
        {
            mouthRoot.gameObject.SetActive(visible);
        }

        if (visible)
        {
            mouthRoot.localScale = new Vector3(1f, opening, 1f);
        }
    }

    private float ReadAudioLevel()
    {
        AudioClip clip = speechAudio.clip;
        if (clip == null || clip.samples == 0 || clip.channels == 0)
        {
            return 0f;
        }

        int frames = Mathf.Min(SampleFrames, clip.samples);
        if (sampledClip != clip || samples == null || samples.Length != frames * clip.channels)
        {
            sampledClip = clip;
            samples = new float[frames * clip.channels];
        }

        int startFrame = Mathf.Clamp(speechAudio.timeSamples, 0, clip.samples - frames);
        if (!clip.GetData(samples, startFrame))
        {
            return 0f;
        }

        float sum = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            sum += samples[i] * samples[i];
        }

        float rms = Mathf.Sqrt(sum / samples.Length);
        return Mathf.Clamp01((rms - 0.008f) * audioSensitivity);
    }

    private void CreateSurface(string name, Material material, Vector3 size, float forwardOffset)
    {
        var surface = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        surface.transform.SetParent(mouthRoot, false);
        surface.transform.localPosition = new Vector3(0f, 0f, forwardOffset);
        // The ellipse faces local -Z, toward a viewer in front of Remy.
        surface.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        surface.transform.localScale = size;
        surface.GetComponent<MeshFilter>().sharedMesh = mouthMesh;
        surface.GetComponent<MeshRenderer>().sharedMaterial = material;
    }

    private static Mesh CreateEllipse()
    {
        var vertices = new Vector3[EllipseSegments + 1];
        var triangles = new int[EllipseSegments * 3];
        for (int i = 0; i < EllipseSegments; i++)
        {
            float angle = i * Mathf.PI * 2f / EllipseSegments;
            vertices[i + 1] = new Vector3(Mathf.Cos(angle) * 0.5f, Mathf.Sin(angle) * 0.5f);
            triangles[i * 3] = 0;
            triangles[i * 3 + 1] = 1 + (i + 1) % EllipseSegments;
            triangles[i * 3 + 2] = i + 1;
        }

        var mesh = new Mesh { name = "Remy Speech Mouth" };
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        return mesh;
    }

    private void OnDisable()
    {
        opening = 0f;
        if (mouthRoot != null)
        {
            mouthRoot.gameObject.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        if (mouthRoot != null)
        {
            Destroy(mouthRoot.gameObject);
        }
        if (mouthMesh != null)
        {
            Destroy(mouthMesh);
        }
    }
}
