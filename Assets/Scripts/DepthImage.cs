using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class DepthImage : MonoBehaviour
{
    public Text info;

    [SerializeField]
    AROcclusionManager m_OcclusionManager;

    [SerializeField]
    ARCameraManager m_CameraManager;

    [SerializeField]
    RawImage m_RawDepthImage;

    [SerializeField]
    RawImage m_RawCameraImage;

    // This is for visualizing depth images
    [SerializeField]
    Material m_DepthMaterial;

    // Depth image data
    public static byte[] depthArray = new byte[0];
    int depthWidth = 0; // (width, height) = (256, 192) on iPhone 12 Pro
    int depthHeight = 0;
    int depthStride = 4; // Should be either 2 or 4
    
    // Camera intrinsics
    Vector2 focalLength = Vector2.zero;
    Vector2 principalPoint = Vector2.zero;

    private Matrix4x4 localToWorldTransform = Matrix4x4.identity;
    private Matrix4x4 screenRotation = Matrix4x4.Rotate(Quaternion.identity);
    private new Camera camera;

    public static Vector3 position;
    public static Vector3 rotation;

    void Awake()
    {
        Application.targetFrameRate = 30;
        QualitySettings.vSyncCount = 0;

        camera = m_CameraManager.GetComponent<Camera>();

        m_CameraManager.frameReceived += OnCameraFrameReceived;

        // Set depth image material
        m_RawDepthImage.material = m_DepthMaterial;

        #if UNITY_EDITOR
        Application.onBeforeRender += OnBeforeRender;
        #endif
    }

    // This is for getting depth images in XR simulation
    void OnBeforeRender()
    {
        if (!m_OcclusionManager.TryGetEnvironmentDepthTexture(out var envDepth))
        {
            return;
        }

        // Show depth image
        Texture2D envDepth2D = envDepth as Texture2D;
        m_RawDepthImage.texture = envDepth2D;

        // Get depth image texture
        RenderTexture rt = RenderTexture.GetTemporary(envDepth.width, envDepth.height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
        Graphics.Blit(envDepth, rt);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(envDepth.width, envDepth.height, TextureFormat.RFloat, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);

        // Convert depth image texture to byte array
        int numBytes = tex.width * tex.height * 4;
        if (depthArray.Length != numBytes)
            depthArray = new byte[numBytes];
        tex.GetRawTextureData().CopyTo(depthArray, 0);

        // Settings for visualizing depth image
        Quaternion rotation = Quaternion.Euler(0, 0, 0);
        Matrix4x4 rotMatrix = Matrix4x4.Rotate(rotation);
        m_RawDepthImage.material.SetMatrix(Shader.PropertyToID("_DisplayRotationPerFrame"), rotMatrix);

        var textureAspectRatio = (float) tex.width / tex.height;
        float minDimension = 480.0f;
        float maxDimension = Mathf.Round(minDimension * textureAspectRatio);
        m_RawDepthImage.rectTransform.sizeDelta = new Vector2(maxDimension, minDimension);
    }

    void OnCameraFrameReceived(ARCameraFrameEventArgs args)
    {
        #if !UNITY_EDITOR
        UpdateDepthImages();
        #endif
        UpdateCameraImage();
    }

    // This is called every frame
    void Update()
    {
        // Show the FPS
        info.text = $"{Convert.ToInt32(1.0 / Time.unscaledDeltaTime)}\n";

        // Update camera info
        position = camera.transform.position;
        rotation = camera.transform.rotation.eulerAngles;
        screenRotation = Matrix4x4.Rotate(Quaternion.Euler(0, 0, GetRotationForScreen()));
        if (camera.transform.localToWorldMatrix != Matrix4x4.identity)
            localToWorldTransform = camera.transform.localToWorldMatrix * screenRotation;

        ProcessDepthImage();
    }

    void ProcessDepthImage()
    {
        if (depthArray == null || depthArray.Length == 0)
            return;
        Debug.Log("Distance at pixel (0,0): " + GetDepth(0,0));
    }

    private bool UpdateDepthImages()
    {
        bool success = false;

        // Acquire a depth image and update the corresponding raw image.
        if (m_OcclusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage image)) {
            using (image) {
                UpdateRawImage(m_RawDepthImage, image, image.format.AsTextureFormat(), true);

                // Get distance data into depthArray
                depthWidth = image.width;
                depthHeight = image.height;
                UpdateCameraParams();

                int numPixels = depthWidth * depthHeight;
                Debug.Assert(image.planeCount == 1, "Plane count is not 1");
                depthStride = image.GetPlane(0).pixelStride;
                int numBytes = numPixels * depthStride;
                if (depthArray.Length != numBytes)
                    depthArray = new byte[numBytes];
                image.GetPlane(0).data.CopyTo(depthArray);

                success = true;
            }
        }

        return success;
    }

    // Access RGB camera images
    private void UpdateCameraImage()
    {
        // Acquire a camera image, update the corresponding raw image, and do CV
        if (m_CameraManager.TryAcquireLatestCpuImage(out XRCpuImage cameraImage)) {
            using (cameraImage) {
                UpdateRawImage(m_RawCameraImage, cameraImage, TextureFormat.RGBA32, false);
                // StartCoroutine(yolo.ExecuteML(m_RawCameraImage.texture));
            }
        }
    }

    private void UpdateRawImage(RawImage rawImage, XRCpuImage cpuImage, TextureFormat format, bool isDepth)
    {
        Debug.Assert(rawImage != null, "no raw image");

        // Get the texture associated with the UI.RawImage that we wish to display on screen.
        var texture = rawImage.texture as Texture2D;

        // If the texture hasn't yet been created, or if its dimensions have changed, (re)create the texture.
        // Note: Although texture dimensions do not normally change frame-to-frame, they can change in response to
        //    a change in the camera resolution (for camera images) or changes to the quality of the human depth
        //    and human stencil buffers.
        if (texture == null || texture.width != cpuImage.width || texture.height != cpuImage.height)
        {
            texture = new Texture2D(cpuImage.width, cpuImage.height, format, false);
            rawImage.texture = texture;
        }

        var conversionParams = new XRCpuImage.ConversionParams(cpuImage, format, XRCpuImage.Transformation.None);

        // Get the Texture2D's underlying pixel buffer.
        var rawTextureData = texture.GetRawTextureData<byte>();

        // Make sure the destination buffer is large enough to hold the converted data (they should be the same size)
        Debug.Assert(rawTextureData.Length == cpuImage.GetConvertedDataSize(conversionParams.outputDimensions, conversionParams.outputFormat),
            "The Texture2D is not the same size as the converted data.");

        // Perform the conversion.
        cpuImage.Convert(conversionParams, rawTextureData);

        // "Apply" the new pixel data to the Texture2D.
        texture.Apply();

        // Get the aspect ratio for the current texture.
        var textureAspectRatio = (float)texture.width / texture.height;

        // Determine the raw image rectSize preserving the texture aspect ratio, matching the screen orientation,
        // and keeping a minimum dimension size.
        float minDimension = 480.0f;
        float maxDimension = Mathf.Round(minDimension * textureAspectRatio);
        Vector2 rectSize;
        if (isDepth) {
            switch (Screen.orientation)
            {
                case ScreenOrientation.LandscapeRight:
                case ScreenOrientation.LandscapeLeft:
                    maxDimension = Screen.width;
                    minDimension = Mathf.Round(maxDimension / textureAspectRatio);
                    rectSize = new Vector2(maxDimension, minDimension);
                    break;
                case ScreenOrientation.PortraitUpsideDown:
                case ScreenOrientation.Portrait:
                default:
                    maxDimension = Screen.height;
                    minDimension = Mathf.Round(maxDimension / textureAspectRatio);
                    rectSize = new Vector2(minDimension, maxDimension);
                    break;
            }
            rawImage.rectTransform.sizeDelta = rectSize;

            // Rotate the depth material to match screen orientation.
            Quaternion rotation = Quaternion.Euler(0, 0, GetRotation());
            Matrix4x4 rotMatrix = Matrix4x4.Rotate(rotation);
            m_RawDepthImage.material.SetMatrix(Shader.PropertyToID("_DisplayRotationPerFrame"), rotMatrix);
        }
        else {
            rectSize = new Vector2(maxDimension, minDimension);
            rawImage.rectTransform.sizeDelta = rectSize;
        }
    }

    // Obtain the depth value in meters. (x,y) are pixel coordinates
    // In portrait mode, (0, 0) is top right, (depthWidth, depthHeight) is bottom left.
    // Screen orientation does not change coordinate locations on the screen.
    public float GetDepth(int x, int y)
    {
        int index = (y * depthWidth) + x;
        float depthInMeters = 0;
        if (depthStride == 4) // DepthFloat32
            depthInMeters = BitConverter.ToSingle(depthArray, depthStride * index);
        else if (depthStride == 2) // DepthUInt16
            depthInMeters = BitConverter.ToUInt16(depthArray, depthStride * index) * 0.001f;

        if (depthInMeters > 0) {
            return depthInMeters;
        }

        return 99999f;
    }

    // Given image pixel coordinates (x,y) and distance z, returns a vertex in local camera space.
    // In portrait mode, x and y will be swapped, i.e. +x is down and +y is right
    public Vector3 ComputeVertex(int x, int y, float z)
    {
        Vector3 vertex = Vector3.negativeInfinity;
        if (z > 0) {
            float vertex_x = (x - principalPoint.x) * z / focalLength.x;
            float vertex_y = (y - principalPoint.y) * z / focalLength.y;
            vertex.x = vertex_x;
            vertex.y = -vertex_y;
            vertex.z = z;
        }
        return vertex;
    }

    // Transforms a vertex in local space to world space
    public Vector3 TransformLocalToWorld(Vector3 vertex)
    {
        return localToWorldTransform.MultiplyPoint(vertex);
    }

    public static int GetRotation() => Screen.orientation switch
    {
        ScreenOrientation.Portrait => 90,
        ScreenOrientation.LandscapeLeft => 180,
        ScreenOrientation.PortraitUpsideDown => -90,
        ScreenOrientation.LandscapeRight => 0,
        _ => 90
    };

    public static int GetRotationForScreen() => Screen.orientation switch
    {
        ScreenOrientation.Portrait => -90,
        ScreenOrientation.LandscapeLeft => 0,
        ScreenOrientation.PortraitUpsideDown => 90,
        ScreenOrientation.LandscapeRight => 180,
        _ => -90
    };

    private void UpdateCameraParams()
    {
        // Gets the camera parameters to create the required number of vertices.
        if (m_CameraManager.TryGetIntrinsics(out XRCameraIntrinsics cameraIntrinsics))
        {
            // Scales camera intrinsics to the depth map size.
            Vector2 intrinsicsScale;
            intrinsicsScale.x = depthWidth / (float)cameraIntrinsics.resolution.x;
            intrinsicsScale.y = depthHeight / (float)cameraIntrinsics.resolution.y;

            focalLength = MultiplyVector2(cameraIntrinsics.focalLength, intrinsicsScale);
            principalPoint = MultiplyVector2(cameraIntrinsics.principalPoint, intrinsicsScale);
            focalLength.y = focalLength.x;
        }
    }

    private Vector2 MultiplyVector2(Vector2 v1, Vector2 v2)
    {
        return new Vector2(v1.x * v2.x, v1.y * v2.y);
    }
}