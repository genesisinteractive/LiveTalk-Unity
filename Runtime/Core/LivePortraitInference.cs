using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace LiveTalk.Core
{
    using API;
    using Utils;

    /// <summary>
    /// LivePortrait prediction state for frame-by-frame inference.
    /// Maintains landmarks and motion information between frames for consistent animation.
    /// </summary>
    internal class LivePortraitPredInfo
    {
        /// <summary>
        /// Facial landmarks from the previous frame for tracking continuity
        /// </summary>
        public Vector2[] Landmarks { get; set; }  // lmk
        
        /// <summary>
        /// Initial motion information from the first driving frame for reference
        /// </summary>
        public MotionInfo InitialMotionInfo { get; set; }  // x_d_0_info
    }

    /// <summary>
    /// Core LivePortrait inference engine for real-time facial animation and expression transfer.
    /// Provides comprehensive functionality for face detection, motion extraction, keypoint transformation,
    /// and neural rendering to create animated talking head videos from driving frames.
    /// </summary>
    internal class LivePortraitInference : IDisposable
    {
        #region Private Fields
        private static readonly string MODEL_RELATIVE_PATH = "LivePortrait";
        
        // ONNX Models
        private Model _appearanceFeatureExtractor;  // 3D appearance feature extraction
        private Model _motionExtractor;             // motion parameters extraction
        private Model _stitching;                   // keypoint stitching refinement
        private Model _warpingSpade;               // neural warping and rendering
        private FaceAnalysis _faceAnalysis;        // face detection and analysis
        
        // Configuration and State
        private readonly LiveTalkConfig _config;
        private bool _initialized = false;
        private bool _disposed = false;
        
        // Templates and Resources
        private Frame _maskTemplate;

        #endregion

        #region Properties

        /// <summary>
        /// Gets a value indicating whether the LivePortrait inference engine is initialized and ready for use.
        /// </summary>
        public bool IsInitialized => _initialized;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the LivePortraitInference class with the specified configuration.
        /// Loads the mask template and prepares prediction state for frame-by-frame inference.
        /// </summary>
        /// <param name="config">The LiveTalk configuration containing model paths and inference settings</param>
        /// <exception cref="ArgumentNullException">Thrown when config is null</exception>
        public LivePortraitInference(LiveTalkConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            
            try
            {
                if (_config.MemoryUsage != MemoryUsage.Optimal)
                {
                    LoadMaskTemplate();
                }
                InitializeModels();
                Logger.LogVerbose("[LivePortraitInference] Instance created successfully");
            }
            catch (Exception e)
            {
                Logger.LogError($"[LivePortraitInference] Failed to initialize: {e.Message}");
                _initialized = false;
                throw new InvalidOperationException($"LivePortrait initialization failed: {e.Message}", e);
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Waits for all models to be fully loaded.
        /// </summary>
        /// <returns>A task that completes when all models are loaded</returns>
        public async Task WaitForAllModelsAsync()
        {
            var tasks = new List<Task>();
            
            if (_appearanceFeatureExtractor?.LoadTask != null) tasks.Add(_appearanceFeatureExtractor.LoadTask);
            
            if (_motionExtractor?.LoadTask != null) tasks.Add(_motionExtractor.LoadTask);
            
            if (_stitching?.LoadTask != null) tasks.Add(_stitching.LoadTask);
            
            if (_warpingSpade?.LoadTask != null) tasks.Add(_warpingSpade.LoadTask);
            
            if (_faceAnalysis != null) tasks.Add(_faceAnalysis.WaitForAllModelsAsync());
            
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
            }
        }

        /// <summary>
        /// Asynchronously generates animated frames from a source image using driving frames for motion transfer.
        /// This method performs complete LivePortrait inference including source processing, frame-by-frame animation,
        /// and streaming output generation using Unity coroutines for real-time performance.
        /// </summary>
        /// <param name="sourceImage">The source image texture containing the face to animate</param>
        /// <param name="outputStream">The output stream to receive generated animated frames</param>
        /// <param name="drivingStream">The input stream providing driving frames for motion transfer</param>
        /// <returns>An enumerator for Unity coroutine execution</returns>
        /// <exception cref="ArgumentNullException">Thrown when any parameter is null</exception>
        /// <exception cref="InvalidOperationException">Thrown when face detection or model initialization fails</exception>
        public IEnumerator GenerateAsync(
            Texture2D sourceImage,
            FrameStream outputStream,
            FrameStream drivingStream)
        {
            // Validate parameters
            if (sourceImage == null)
                throw new ArgumentNullException(nameof(sourceImage));
            if (outputStream == null)
                throw new ArgumentNullException(nameof(outputStream));
            if (drivingStream == null)
                throw new ArgumentNullException(nameof(drivingStream));

            int processedFrames = 0;

            // Every exit — normal, driving stream fault, or a bridged task
            // rethrowing — must mark the output finished, or the consumer
            // draining it waits forever. finally is the only construct an
            // iterator may wrap a yield in, and it is enough.
            try
            {
                if (_config.MemoryUsage == MemoryUsage.Optimal)
                {
                    LoadMaskTemplate();
                }

                // Convert source image to RGB24 format and process
                sourceImage = TextureUtils.ConvertTexture2DToRGB24(sourceImage);
                var srcImg = TextureUtils.Texture2DToFrame(sourceImage);

                // Start session
                Logger.LogVerbose("[LivePortraitMuseTalkAPI] Starting Model sessions");
                yield return TaskYield.Wait(StartSession(), "LivePortraitInference.StartSession");

                // Process source image
                Logger.LogVerbose("[LivePortraitMuseTalkAPI] Source image processing started asynchronously");
                ProcessSourceImageResult processResult = null;
                yield return TaskYield.Wait(ProcessSourceImageAsync(srcImg), r => processResult = r,
                    "LivePortraitInference.ProcessSourceImage");

                Logger.LogVerbose("[LivePortraitMuseTalkAPI] Source image processing completed, starting frame processing pipeline");

                var predInfo = new LivePortraitPredInfo
                {
                    Landmarks = null,
                    InitialMotionInfo = null
                };

                while (processedFrames < drivingStream.TotalExpectedFrames && drivingStream.HasMoreFrames)
                {
                    var awaiter = drivingStream.WaitForNext();
                    yield return awaiter;
                    var drivingFrame = awaiter.Texture;

                    if (drivingFrame != null)
                    {
                        var imgRgbData = TextureUtils.Texture2DToFrame(drivingFrame);
                        UnityEngine.Object.DestroyImmediate(drivingFrame);

                        (Frame generatedImg, LivePortraitPredInfo updatedPredInfo, float[] pose) prediction = default;
                        yield return TaskYield.Wait(ProcessNextFrameAsync(processResult, predInfo, imgRgbData),
                            r => prediction = r, "LivePortraitInference.ProcessNextFrame");
                        predInfo = prediction.updatedPredInfo;

                        if (prediction.generatedImg.data != null)
                        {
                            var generatedImgTexture = TextureUtils.FrameToTexture2D(prediction.generatedImg);
                            // Pose before frame: a consumer that reads Poses on
                            // dequeue must find this frame's entry already there.
                            outputStream.Poses?.Add(prediction.pose);
                            outputStream.Queue.Enqueue(generatedImgTexture);
                            Logger.LogVerbose($"[LivePortraitMuseTalkAPI] Processed frame {processedFrames + 1}/{drivingStream.TotalExpectedFrames}");
                        }

                        processedFrames++;
                    }
                }

                // The driving loader finishing early because it failed is a
                // failure of this animation too — a short, silently truncated
                // clip is worse than an error that names the cause.
                if (drivingStream.Error != null)
                {
                    throw new InvalidOperationException(
                        $"Driving frames failed to load after {processedFrames} frame(s): {drivingStream.Error.Message}",
                        drivingStream.Error);
                }

                Logger.LogVerbose($"[LivePortraitMuseTalkAPI] Pipelined processing completed: {processedFrames} frames generated");
            }
            finally
            {
                outputStream.Finished = true;
                EndSession();

                if (_config.MemoryUsage == MemoryUsage.Optimal)
                {
                    _maskTemplate = Frame.Zero;
                }
            }
        }

        #endregion

        #region Private Methods - Model Initialization
        
        /// <summary>
        /// Initializes all ONNX models required for LivePortrait inference.
        /// Loads appearance feature extractor, motion extractor, stitching, warping SPADE, and face analysis models.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when any model fails to initialize</exception>
        private void InitializeModels()
        {
            if (_initialized)
            {
                return;
            }

            bool forceFP32 = _config.MemoryUsage == MemoryUsage.Quality;
            Precision fp16Precision = forceFP32 ? Precision.FP32 : Precision.FP16;

            Logger.LogVerbose("[LivePortraitInference] Initializing models...");
            _appearanceFeatureExtractor = new Model(_config, "appearance_feature_extractor", MODEL_RELATIVE_PATH, ExecutionProvider.CoreML);
            _motionExtractor = new Model(_config, "motion_extractor", MODEL_RELATIVE_PATH, ExecutionProvider.CoreML);
            _stitching = new Model(_config, "stitching", MODEL_RELATIVE_PATH);
            _warpingSpade = new Model(_config, "warping_spade", MODEL_RELATIVE_PATH, ExecutionProvider.CoreML, fp16Precision);
            _faceAnalysis = FaceAnalysis.CreateOrGetInstance(_config);
            _initialized = true;
        }

        /// <summary>
        /// Starts the session for all models
        /// </summary>
        private async Task StartSession()
        {
            await _appearanceFeatureExtractor.StartSession();
            await _motionExtractor.StartSession();
            await _stitching.StartSession();
            await _warpingSpade.StartSession();
            await _faceAnalysis.StartFaceAnalysisSession();
        }

        /// <summary>
        /// Ends the session for all models
        /// </summary>
        private void EndSession()
        {
            _appearanceFeatureExtractor.EndSession();
            _motionExtractor.EndSession();
            _stitching.EndSession();
            _warpingSpade.EndSession();
            _faceAnalysis.EndFaceAnalysisSession();
        }

        #endregion

        #region Private Methods - Source Image Processing

        /// <summary>
        /// Asynchronously processes the source image to extract all necessary features for animation.
        /// This includes face detection, cropping, feature extraction, motion analysis, and keypoint transformation.
        /// </summary>
        /// <param name="frame">The source image frame to process</param>
        /// <returns>A task containing the complete source image processing result with all extracted features</returns>
        /// <exception cref="InvalidOperationException">Thrown when face detection or feature extraction fails</exception>
        private async Task<ProcessSourceImageResult> ProcessSourceImageAsync(Frame frame)
        {
            return await Task.Run(async () => {
                // Step 1: Preprocess source image (resize and ensure even dimensions)
                var srcImg = SrcPreprocess(frame);
                
                // Step 2: Detect face and crop to standard size
                var cropInfo = await CropSrcImage(srcImg);
                var Is = NormalizeFrame(cropInfo.ImageCrop256x256);
                
                // Step 3: Extract motion parameters (pose, expression, etc.)
                var xSInfo = await GetKpInfo(Is);
                var Rs = MathUtils.GetRotationMatrix(xSInfo.Pitch, xSInfo.Yaw, xSInfo.Roll);
                
                // Step 4: Extract 3D appearance features
                var fs = await ExtractFeature3d(Is);
                
                // Step 5: Transform keypoints to canonical space
                var xs = TransformKeypoint(xSInfo);
                
                // Step 6: Prepare paste-back mask for final compositing
                var maskOri = PreparePasteBack(cropInfo.Transform, srcImg.width, srcImg.height);

                return new ProcessSourceImageResult
                {
                    CropInfo = cropInfo,
                    SrcImg = srcImg,
                    MaskOri = maskOri,
                    XsInfo = xSInfo,
                    Rs = Rs,
                    Fs = fs,
                    Xs = xs
                };
            });
        }

        /// <summary>
        /// Load mask template texture from Resources or StreamingAssets
        /// </summary>
        private void LoadMaskTemplate()
        {
            try
            {
                // First try to load from Resources
                var maskTexture = Resources.Load<Texture2D>("mask_template");
                if (maskTexture != null)
                {
                    Logger.LogVerbose("[ModelUtils] Loaded mask template from Resources");
                    _maskTemplate = TextureUtils.Texture2DToFrame(TextureUtils.ConvertTexture2DToRGB24(maskTexture));
                    return;
                }
                
                // Try to load from StreamingAssets
                string maskPath = Path.Combine(Application.streamingAssetsPath, "mask_template.png");
                if (File.Exists(maskPath))
                {
                    byte[] fileData = File.ReadAllBytes(maskPath);
                    var texture = new Texture2D(2, 2);
                    if (texture.LoadImage(fileData))
                    {                        
                        Logger.LogVerbose("[ModelUtils] Loaded mask template from StreamingAssets");
                        _maskTemplate = TextureUtils.Texture2DToFrame(TextureUtils.ConvertTexture2DToRGB24(texture));
                        return;
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                    }
                }
                
                // Try to load from config model path
                if (!string.IsNullOrEmpty(_config?.ModelPath))
                {
                    string configMaskPath = Path.Combine(_config.ModelPath, "mask_template.png");
                    if (File.Exists(configMaskPath))
                    {
                        byte[] fileData = File.ReadAllBytes(configMaskPath);
                        var texture = new Texture2D(2, 2);
                        if (texture.LoadImage(fileData))
                        {
                            Logger.LogVerbose($"[ModelUtils] Loaded mask template from config path: {configMaskPath}");
                            _maskTemplate = TextureUtils.Texture2DToFrame(TextureUtils.ConvertTexture2DToRGB24(texture));
                            return;
                        }
                        else
                        {
                            UnityEngine.Object.DestroyImmediate(texture);
                        }
                    }
                }
                
                Logger.LogWarning("[ModelUtils] Could not find mask_template.png in Resources, StreamingAssets, or config path. Will use default mask.");
            }
            catch (Exception e)
            {
                Logger.LogError($"[ModelUtils] Error loading mask template: {e.Message}");
            }
        }

        #endregion

        #region Private Methods - Image Preprocessing

        /// <summary>
        /// Preprocesses the source frame by resizing to fit within maximum dimensions and ensuring even pixel dimensions.
        /// This ensures compatibility with neural network models that require specific input constraints.
        /// </summary>
        /// <param name="frame">The source frame to preprocess</param>
        /// <returns>The preprocessed frame with appropriate dimensions</returns>
        private unsafe Frame SrcPreprocess(Frame frame)
        {
            int currentWidth = frame.width;
            int currentHeight = frame.height;
            
            const int maxDim = 1280;
            if (Mathf.Max(currentHeight, currentWidth) > maxDim)
            {
                int newHeight, newWidth;
                if (currentHeight > currentWidth)
                {
                    newHeight = maxDim;
                    newWidth = Mathf.RoundToInt(currentWidth * ((float)maxDim / currentHeight));
                }
                else
                {
                    newWidth = maxDim;
                    newHeight = Mathf.RoundToInt(currentHeight * ((float)maxDim / currentWidth));
                }
                frame = FrameUtils.ResizeFrame(frame, newWidth, newHeight, SamplingMode.Bilinear);
                
                currentWidth = newWidth;
                currentHeight = newHeight;
            }
            const int division = 2;
            int finalHeight = currentHeight - (currentHeight % division);
            int finalWidth = currentWidth - (currentWidth % division);
            
            if (finalHeight == 0 || finalWidth == 0)
            {
                return frame;
            }
            
            if (finalHeight != currentHeight || finalWidth != currentWidth)
            {
                frame = FrameUtils.CropFrame(frame, new Rect(0, 0, finalWidth, finalHeight));
            }
            
            return frame;
        }

        /// <summary>
        /// Asynchronously detects and crops the face from the source image.
        /// Performs face detection, landmark extraction, and creates multiple resolution crops for processing.
        /// </summary>
        /// <param name="frame">The source frame containing the face to crop</param>
        /// <returns>A task containing comprehensive crop information with landmarks and multiple resolution images</returns>
        /// <exception cref="InvalidOperationException">Thrown when no face is detected in the source image</exception>
        private async Task<CropInfo> CropSrcImage(Frame frame)
        {
            var srcFaces = await _faceAnalysis.AnalyzeFaces(frame);
            if (srcFaces.Count == 0)
            {
                throw new InvalidOperationException("No face detected in the source image.");
            }

            if (srcFaces.Count > 1)
            {
                Logger.LogWarning("More than one face detected in the image, only pick one face.");
            }
            
            var srcFace = srcFaces[0];
            var lmk = srcFace.Landmarks106;
            var cropSize = 512;
            var cropInfo = _faceAnalysis.GetCropInfo(frame, lmk, cropSize, 2.3f, -0.125f);
            cropInfo.ImageCrop256x256 = FrameUtils.ResizeFrame(cropInfo.ImageCrop, 256, 256, SamplingMode.Bilinear);

            bool getLandMarkCrops = false; // Not used for now
            if (getLandMarkCrops)
            {
                lmk = await _faceAnalysis.LandmarkRunner(frame, lmk);
                cropInfo.LandmarksCrop = lmk;
                cropInfo.LandmarksCrop256x256 = ScaleLandmarks(cropInfo.LandmarksCrop, 256f / 512f);
            }
            return cropInfo;
        }
        
        /// <summary>
        /// Normalize a frame for neural network input by normalizing pixel values to [0,1] range.
        /// </summary>
        /// <param name="frame">The frame to normalize</param>
        /// <returns>A tensor with normalized pixel values ready for neural network processing</returns>
        private DenseTensor<float> NormalizeFrame(Frame frame)
        {
            return FrameUtils.FrameToTensor(frame, 1.0f / 255.0f, 0.0f);
        }

        #endregion

        #region Private Methods - Motion Analysis

        /// <summary>
        /// Asynchronously extracts motion information (pose, expression, keypoints) from preprocessed image data.
        /// Uses the motion extractor model to analyze facial pose and expression parameters.
        /// </summary>
        /// <param name="preprocessedData">The preprocessed image tensor ready for neural network processing</param>
        /// <returns>A task containing complete motion information including pose angles, translation, expression, and keypoints</returns>
        /// <exception cref="InvalidOperationException">Thrown when motion extraction fails</exception>
        private async Task<MotionInfo> GetKpInfo(DenseTensor<float> preprocessedData)
        {
            var inputs = new List<Tensor<float>>
            {
                preprocessedData
            };

            var results = await _motionExtractor.Run(inputs);
            
            // Output order: pitch, yaw, roll, t, exp, scale, kp.
            var pitchTensor = results[0].AsTensor<float>();
            var yawTensor = results[1].AsTensor<float>();
            var rollTensor = results[2].AsTensor<float>();
            var tTensor = results[3].AsTensor<float>();
            var expTensor = results[4].AsTensor<float>();
            var scaleTensor = results[5].AsTensor<float>();
            var kpTensor = results[6].AsTensor<float>();
            
            var processedPitch = ProcessAngleSoftmaxOptimized(pitchTensor);
            var processedYaw = ProcessAngleSoftmaxOptimized(yawTensor);
            var processedRoll = ProcessAngleSoftmaxOptimized(rollTensor);
            
            return new MotionInfo
            {
                Pitch = processedPitch,
                Yaw = processedYaw,
                Roll = processedRoll,
                Translation = ExtractTensorArrayOptimized(tTensor),
                Expression = ExtractTensorArrayOptimized(expTensor),
                Scale = ExtractTensorArrayOptimized(scaleTensor),
                Keypoints = ExtractTensorArrayOptimized(kpTensor)
            };
        }
        
        /// <summary>
        /// Processes angle logits using optimized softmax computation to extract pose angles in degrees.
        /// Uses unsafe memory operations for maximum performance when processing tensor data.
        /// </summary>
        /// <param name="angleLogitsTensor">The raw angle logits tensor from the motion extractor model</param>
        /// <returns>An array containing the processed angle in degrees</returns>
        private unsafe float[] ProcessAngleSoftmaxOptimized(Tensor<float> angleLogitsTensor)
        {
            var tensorData = angleLogitsTensor as DenseTensor<float>;
            if (tensorData == null)
            {
                throw new InvalidOperationException("ProcessAngleSoftmaxOptimized: Non-DenseTensor type");
            }
            
            // Get direct access to tensor's internal buffer
            var buffer = tensorData.Buffer;
            int length = (int)angleLogitsTensor.Length;
            
            // Access raw memory directly using Span
            var span = buffer.Span;
            
            // Find max value for numerical stability
            float maxVal = float.MinValue;
            for (int i = 0; i < length; i++)
            {
                if (span[i] > maxVal) maxVal = span[i];
            }
            
            float expSum = 0f;
            float weightedSum = 0f;
            
            for (int i = 0; i < length; i++)
            {
                float exp = Mathf.Exp(span[i] - maxVal);
                expSum += exp;
                weightedSum += exp * i; // np.arange(66) gives 0,1,2,...,65
            }
            
            float degree = (weightedSum / expSum) * 3f - 97.5f;
            
            return new float[] { degree };
        }

        /// <summary>
        /// </summary>
        private unsafe float[] ExtractTensorArrayOptimized(Tensor<float> tensor)
        {
            // Try to access DenseTensor buffer directly
            if (tensor is DenseTensor<float> denseTensor)
            {
                var buffer = denseTensor.Buffer;
                var span = buffer.Span;
                int length = (int)tensor.Length;
                var result = new float[length];
                span.CopyTo(result);
                return result;
            }

            throw new InvalidOperationException("ExtractTensorArrayOptimized: Non-DenseTensor type");
        }
        
        /// <summary>
        /// Asynchronously extracts 3D appearance features from preprocessed image data using the appearance feature extractor model.
        /// These features encode the identity and appearance characteristics of the source face.
        /// </summary>
        /// <param name="preprocessedData">The preprocessed image tensor ready for feature extraction</param>
        /// <returns>A task containing the 3D appearance feature tensor</returns>
        /// <exception cref="InvalidOperationException">Thrown when feature extraction fails</exception>
        private async Task<Tensor<float>> ExtractFeature3d(DenseTensor<float> preprocessedData)
        {            
            var inputTensor = preprocessedData;
            var inputs = new List<Tensor<float>>
            {
                inputTensor
            };
            
            var results = await _appearanceFeatureExtractor.Run(inputs);
            var outputTensor = results.First().AsTensor<float>();
            return outputTensor;
        }
        
        /// <summary>
        /// Transforms keypoints from the source image coordinate system using rotation, expression, scale, and translation.
        /// This method applies the full 3D transformation pipeline to convert canonical keypoints to the target pose.
        /// </summary>
        /// <param name="xSInfo">The motion information containing keypoints, rotation angles, expression parameters, scale, and translation</param>
        /// <returns>An array of transformed keypoints with applied rotation, expression, scaling, and translation transformations</returns>
        private float[] TransformKeypoint(MotionInfo xSInfo)
        {
            var kp = xSInfo.Keypoints;
            var pitch = xSInfo.Pitch;
            var yaw = xSInfo.Yaw;
            var roll = xSInfo.Roll;
            var t = xSInfo.Translation;
            var exp = xSInfo.Expression;
            var scale = xSInfo.Scale;
            int numKp = kp.Length / 3;
            var rotMat = MathUtils.GetRotationMatrix(pitch, yaw, roll);
            var kpTransformed = new float[kp.Length];
            for (int i = 0; i < numKp; i++)
            {
                // Get keypoint coordinates
                float x = kp[i * 3 + 0];
                float y = kp[i * 3 + 1];
                float z = kp[i * 3 + 2];
                float newX = x * rotMat[0, 0] + y * rotMat[1, 0] + z * rotMat[2, 0];
                float newY = x * rotMat[0, 1] + y * rotMat[1, 1] + z * rotMat[2, 1];
                float newZ = x * rotMat[0, 2] + y * rotMat[1, 2] + z * rotMat[2, 2];
                if (i * 3 + 2 < exp.Length)
                {
                    newX += exp[i * 3 + 0];
                    newY += exp[i * 3 + 1];
                    newZ += exp[i * 3 + 2];
                }
                
                kpTransformed[i * 3 + 0] = newX;
                kpTransformed[i * 3 + 1] = newY;
                kpTransformed[i * 3 + 2] = newZ;
            }
            if (scale.Length > 0)
            {
                float scaleValue = scale[0];
                for (int i = 0; i < kpTransformed.Length; i++)
                {
                    kpTransformed[i] *= scaleValue;
                }
            }
            if (t.Length >= 2)
            {
                for (int i = 0; i < numKp; i++)
                {
                    kpTransformed[i * 3 + 0] += t[0]; // tx
                    kpTransformed[i * 3 + 1] += t[1]; // ty
                    // Don't add tz to z coordinate
                }
            }
            
            return kpTransformed;
        }

        /// <summary>
        /// Asynchronously processes the next driving frame to generate an animated result.
        /// This method combines source image features with driving frame motion to produce the final animated frame.
        /// </summary>
        /// <param name="processResult">The processed source image result containing features and transformation data</param>
        /// <param name="predInfo">The prediction state information for maintaining frame continuity</param>
        /// <param name="drivingFrame">The current driving frame containing motion information to transfer</param>
        /// <returns>A task containing the generated animated frame and updated prediction state</returns>
        private async Task<(Frame, LivePortraitPredInfo, float[])> ProcessNextFrameAsync(
            ProcessSourceImageResult processResult,
            LivePortraitPredInfo predInfo,
            Frame drivingFrame)
        {
            return await Task.Run(async () => 
            {
                var (Ip, updatedPredInfo, pose) = await Predict(
                    processResult.XsInfo, 
                    processResult.Rs, 
                    processResult.Fs, 
                    processResult.Xs, 
                    drivingFrame, 
                    predInfo);

                Frame drivingImg = PasteBack(
                    Ip,
                    processResult.CropInfo.Transform, 
                    processResult.SrcImg, 
                    processResult.MaskOri);
                return (drivingImg, updatedPredInfo, pose);
            });
        }

        #endregion

        #region Pose rendering (no extractor)

        // The source most recently prepared by RenderPosesAsync, keyed on the
        // texture's native id so a second call for the same portrait skips
        // face analysis + appearance extraction (~1 s).
        private ProcessSourceImageResult _poseSource;
        private Texture2D _poseSourceTexture;

        /// <summary>
        /// Renders frames directly from final driving keypoint vectors — the
        /// 63 floats <c>warping_spade</c> consumes, as recorded in
        /// <see cref="FrameStream.Poses"/> and persisted by an avatar's
        /// <c>motion.bin</c>. No motion extractor runs: the pose <em>is</em>
        /// the extractor's output. This is what makes a pose that never
        /// existed in a driving clip — a blend between two authored frames,
        /// a peak held with the idle clip's micro-motion — renderable.
        ///
        /// One session for the whole batch; the source is prepared once per
        /// portrait and reused across calls. <paramref name="onFrame"/> is
        /// called on the main thread with the frame index and the pasted-back
        /// full-size frame; the caller owns the texture.
        /// </summary>
        public IEnumerator RenderPosesAsync(
            Texture2D sourceImage,
            IReadOnlyList<float[]> poses,
            Action<int, Texture2D> onFrame)
        {
            if (sourceImage == null) throw new ArgumentNullException(nameof(sourceImage));
            if (poses == null) throw new ArgumentNullException(nameof(poses));
            if (onFrame == null) throw new ArgumentNullException(nameof(onFrame));

            try
            {
                if (_config.MemoryUsage == MemoryUsage.Optimal)
                    LoadMaskTemplate();

                yield return TaskYield.Wait(StartSession(), "LivePortraitInference.RenderPoses.StartSession");

                if (_poseSource == null || !ReferenceEquals(_poseSourceTexture, sourceImage) || _poseSourceTexture == null)
                {
                    var rgb = TextureUtils.ConvertTexture2DToRGB24(sourceImage);
                    var srcFrame = TextureUtils.Texture2DToFrame(rgb);
                    ProcessSourceImageResult prepared = null;
                    yield return TaskYield.Wait(ProcessSourceImageAsync(srcFrame), r => prepared = r,
                        "LivePortraitInference.RenderPoses.ProcessSourceImage");
                    _poseSource = prepared;
                    _poseSourceTexture = sourceImage;
                }

                var source = _poseSource;
                for (int i = 0; i < poses.Count; i++)
                {
                    var pose = poses[i];
                    if (pose == null || pose.Length != 21 * 3)
                        throw new ArgumentException($"Pose {i} must be 63 floats (21 keypoints × xyz); got {pose?.Length ?? 0}.");

                    Frame rendered = default;
                    yield return TaskYield.Wait(RenderPoseAsync(source, pose), r => rendered = r,
                        $"LivePortraitInference.RenderPose[{i}]");
                    onFrame(i, TextureUtils.FrameToTexture2D(rendered));
                }
            }
            finally
            {
                EndSession();
                if (_config.MemoryUsage == MemoryUsage.Optimal)
                    _maskTemplate = Frame.Zero;
            }
        }

        /// <summary>
        /// Drops the cached prepared source (a new portrait, or memory).
        /// </summary>
        public void ForgetPoseSource()
        {
            _poseSource = null;
            _poseSourceTexture = null;
        }

        private async Task<Frame> RenderPoseAsync(ProcessSourceImageResult source, float[] pose)
        {
            return await Task.Run(async () =>
            {
                var output = await WarpingSpade(source.Fs, source.Xs, pose);
                var crop = FrameUtils.TensorToFrame(output, 0, 1);
                return PasteBack(crop, source.CropInfo.Transform, source.SrcImg, source.MaskOri);
            });
        }

        #endregion

        #region Private Methods - Prediction
        
        /// <summary>
        /// Asynchronously predicts and generates an animated frame from the driving image.
        /// This is the core prediction pipeline that processes motion transfer, keypoint transformation,
        /// stitching refinement, and neural rendering to create the final animated result.
        /// </summary>
        /// <param name="xSInfo">Motion information extracted from the source image</param>
        /// <param name="Rs">Rotation matrix from the source image</param>
        /// <param name="fs">3D appearance features from the source image</param>
        /// <param name="xs">Transformed keypoints from the source image</param>
        /// <param name="img">The current driving frame</param>
        /// <param name="predInfo">Prediction state information for frame continuity</param>
        /// <returns>A task containing the generated animated frame and updated prediction info</returns>
        /// <exception cref="InvalidOperationException">Thrown when face detection or prediction fails</exception>
        private async Task<(Frame, LivePortraitPredInfo, float[])> Predict(
            MotionInfo xSInfo, float[,] Rs, Tensor<float> fs, float[] xs, 
            Frame img, LivePortraitPredInfo predInfo)
        {
            bool frame0 = predInfo.Landmarks == null;
            Vector2[] lmk;
            if (frame0)
            {
                var srcFaces = await _faceAnalysis.AnalyzeFaces(img);
                if (srcFaces.Count == 0)
                {
                    throw new InvalidOperationException("No face detected in the frame");
                }
                
                if (srcFaces.Count > 1)
                {
                    Logger.LogWarning("More than one face detected in the driving frame, only pick one face.");
                }
                
                var srcFace = srcFaces[0];
                lmk = srcFace.Landmarks106;
                lmk = await _faceAnalysis.LandmarkRunner(img, lmk);
            }
            else
            {
                lmk = await _faceAnalysis.LandmarkRunner(img, predInfo.Landmarks);
            }
            
            predInfo.Landmarks = lmk;
            var img256 = FrameUtils.ResizeFrame(img, 256, 256, SamplingMode.Bilinear);
            var Id = NormalizeFrame(img256);
            var xDInfo = await GetKpInfo(Id);
            var Rd = MathUtils.GetRotationMatrix(xDInfo.Pitch, xDInfo.Yaw, xDInfo.Roll);
            xDInfo.RotationMatrix = Rd;
            
            if (frame0)
            {
                predInfo.InitialMotionInfo = xDInfo;
            }
            
            var xD0Info = predInfo.InitialMotionInfo;
            var Rd0 = xD0Info.RotationMatrix;
            
            var Rd0Transposed = MathUtils.TransposeMatrix(Rd0);
            var RdTimesRd0T = MathUtils.MatrixMultiply(Rd, Rd0Transposed);
            var RNew = MathUtils.MatrixMultiply(RdTimesRd0T, Rs);
            
            var expDiff = MathUtils.SubtractArrays(xDInfo.Expression, xD0Info.Expression);
            var deltaNew = MathUtils.AddArrays(xSInfo.Expression, expDiff);
            
            var scaleDiff = MathUtils.DivideArrays(xDInfo.Scale, xD0Info.Scale);
            var scaleNew = MathUtils.MultiplyArrays(xSInfo.Scale, scaleDiff);
            
            var tDiff = MathUtils.SubtractArrays(xDInfo.Translation, xD0Info.Translation);
            var tNew = MathUtils.AddArrays(xSInfo.Translation, tDiff);
            
            if (tNew.Length >= 3) tNew[2] = 0;
            
            var xCs = xSInfo.Keypoints;
            
            var xDNew = CalculateNewKeypoints(xCs, RNew, deltaNew, scaleNew, tNew);
            xDNew = await Stitching(xs, xDNew);
            var output = await WarpingSpade(fs, xs, xDNew);
            var resultTexture = FrameUtils.TensorToFrame(output, 0, 1);
            // xDNew is the pose this frame was rendered from; RenderPosesAsync
            // reproduces the frame from it alone.
            return (resultTexture, predInfo, xDNew);
        }
        
        /// <summary>
        /// Asynchronously refines driving keypoints using the stitching model for better temporal consistency.
        /// This process helps maintain smooth transitions between frames and reduces jitter in the animation.
        /// </summary>
        /// <param name="kpSource">The source keypoints from the original face</param>
        /// <param name="kpDriving">The driving keypoints from the current frame</param>
        /// <returns>A task containing the refined driving keypoints with improved consistency</returns>
        /// <exception cref="InvalidOperationException">Thrown when keypoint stitching fails</exception>
        private async Task<float[]> Stitching(float[] kpSource, float[] kpDriving)
        {
            var kpDrivingNew = new float[kpDriving.Length];
            Array.Copy(kpDriving, kpDrivingNew, kpDriving.Length);
            
            var feat = new float[kpSource.Length + kpDriving.Length];
            Array.Copy(kpSource, 0, feat, 0, kpSource.Length);
            Array.Copy(kpDriving, 0, feat, kpSource.Length, kpDriving.Length);
            
            var inputTensor = new DenseTensor<float>(feat, new[] { 1, feat.Length });
            
            var inputs = new List<Tensor<float>>
            {
                inputTensor
            };
            
            var results = await _stitching.Run(inputs);
            var delta = results.First().AsTensor<float>().ToArray();
            
            int numKp = kpDriving.Length / 3;
            for (int i = 0; i < numKp * 3 && i < delta.Length; i++)
            {
                kpDrivingNew[i] += delta[i];
            }
            
            if (delta.Length >= numKp * 3 + 2)
            {
                float deltaX = delta[numKp * 3];
                float deltaY = delta[numKp * 3 + 1];
                
                for (int i = 0; i < numKp; i++)
                {
                    kpDrivingNew[i * 3] += deltaX;     // x coordinate
                    kpDrivingNew[i * 3 + 1] += deltaY; // y coordinate
                }
            }
            
            return kpDrivingNew;
        }
        
        /// <summary>
        /// Asynchronously performs neural warping and rendering using the SPADE-based warping model.
        /// This is the core rendering step that generates the final animated face by warping the 3D features
        /// according to the motion defined by the keypoint differences.
        /// </summary>
        /// <param name="feature3d">The 3D appearance features extracted from the source image</param>
        /// <param name="kpSource">The source keypoints defining the canonical face pose</param>
        /// <param name="kpDriving">The driving keypoints defining the target face pose</param>
        /// <returns>A task containing the rendered face tensor</returns>
        /// <exception cref="InvalidOperationException">Thrown when neural warping fails</exception>
        private async Task<Tensor<float>> WarpingSpade(Tensor<float> feature3d, float[] kpSource, float[] kpDriving)
        {            
            // Verify expected sizes
            int expectedFeature3DSize = 1 * 32 * 16 * 64 * 64; // 2,097,152
            int expectedKpSize = 21 * 3; // 63 (21 keypoints * 3 coordinates)
            
            if (feature3d.Length != expectedFeature3DSize)
            {
                Logger.LogError($"[LivePortrait] warping_spade feature3d size mismatch: expected {expectedFeature3DSize}, got {feature3d.Length}");
            }
            
            if (kpSource.Length != expectedKpSize || kpDriving.Length != expectedKpSize)
            {
                Logger.LogError($"[LivePortrait] warping_spade keypoint size mismatch: expected {expectedKpSize}, got kpSource {kpSource.Length}, kpDriving {kpDriving.Length}");
            }
            
            // Create tensors with proper shapes
            var feature3DTensor = feature3d;
            var kpSourceTensor = new DenseTensor<float>(kpSource, new[] { 1, kpSource.Length / 3, 3 });
            var kpDrivingTensor = new DenseTensor<float>(kpDriving, new[] { 1, kpDriving.Length / 3, 3 });
            
            var inputs = new List<Tensor<float>>
            {
                feature3DTensor,  // feature_3d
                kpDrivingTensor, // kp_driving  
                kpSourceTensor   // kp_source
            };
            
            var results = await _warpingSpade.Run(inputs);
            
            return results[0].AsTensor<float>();
        }
        
        /// <summary>
        /// Scales an array of landmark points by a uniform scaling factor.
        /// This method is used to adjust landmark coordinates when resizing images during preprocessing.
        /// </summary>
        /// <param name="landmarks">The array of landmark points to scale</param>
        /// <param name="scale">The uniform scaling factor to apply to all landmark coordinates</param>
        /// <returns>A new array of scaled landmark points</returns>
        private Vector2[] ScaleLandmarks(Vector2[] landmarks, float scale)
        {
            var result = new Vector2[landmarks.Length];
            for (int i = 0; i < landmarks.Length; i++)
            {
                result[i] = landmarks[i] * scale;
            }
            return result;
        }

        /// <summary>
        /// Calculates new keypoint positions by applying rotation, expression changes, scaling, and translation.
        /// This method transforms canonical source keypoints to match the driving frame's motion parameters.
        /// </summary>
        /// <param name="xCs">The canonical source keypoints in 3D space</param>
        /// <param name="RNew">The rotation matrix to apply to the keypoints</param>
        /// <param name="deltaNew">The expression change vector to add to the keypoints</param>
        /// <param name="scaleNew">The scaling factors to apply to the transformed keypoints</param>
        /// <param name="tNew">The translation vector to apply as final positioning</param>
        /// <returns>An array of transformed keypoints with applied rotation, expression, scale, and translation</returns>
        private float[] CalculateNewKeypoints(float[] xCs, float[,] RNew, float[] deltaNew, float[] scaleNew, float[] tNew)
        {
            int numKp = xCs.Length / 3;
            var result = new float[xCs.Length];
            
            for (int kp = 0; kp < numKp; kp++)
            {
                float x = xCs[kp * 3 + 0];
                float y = xCs[kp * 3 + 1]; 
                float z = xCs[kp * 3 + 2];
                
                float newX = x * RNew[0, 0] + y * RNew[1, 0] + z * RNew[2, 0];
                float newY = x * RNew[0, 1] + y * RNew[1, 1] + z * RNew[2, 1];
                float newZ = x * RNew[0, 2] + y * RNew[1, 2] + z * RNew[2, 2];
                
                if (kp * 3 + 2 < deltaNew.Length)
                {
                    newX += deltaNew[kp * 3 + 0];
                    newY += deltaNew[kp * 3 + 1];
                    newZ += deltaNew[kp * 3 + 2];
                }
                
                if (scaleNew.Length > 0)
                {
                    newX *= scaleNew[0];
                    newY *= scaleNew[0];
                    newZ *= scaleNew[0];
                }
                
                if (tNew.Length >= 3)
                {
                    newX += tNew[0];
                    newY += tNew[1];
                    newZ += tNew[2];
                }
                
                result[kp * 3 + 0] = newX;
                result[kp * 3 + 1] = newY;
                result[kp * 3 + 2] = newZ;
            }
            
            return result;
        }
        
        /// <summary>
        /// Prepares a paste-back mask by transforming the mask template to match the original image dimensions.
        /// This mask is used to seamlessly blend the generated face back into the original image.
        /// </summary>
        /// <param name="cropMc2o">The transformation matrix from crop coordinates to original image coordinates</param>
        /// <param name="width">The width of the target original image</param>
        /// <param name="height">The height of the target original image</param>
        /// <returns>A transformed mask frame that defines the blending region for paste-back operation</returns>
        /// <exception cref="Exception">Thrown when no mask template is available</exception>
        private Frame PreparePasteBack(Matrix4x4 cropMc2o, int width, int height)
        {
            if (_maskTemplate.data == null)
            {
                Logger.LogError("[PreparePasteBack] No mask template provided, creating default circular mask");
                throw new Exception("[PreparePasteBack] No mask template provided");
            }
            
            // Transform mask template using crop transformation matrix
            float[,] M = new float[,] {
                { cropMc2o.m00, cropMc2o.m01, cropMc2o.m03 },
                { cropMc2o.m10, cropMc2o.m11, cropMc2o.m13 }
            };
            
            var maskOri = FrameUtils.AffineTransformFrame(_maskTemplate, M, width, height);
            return maskOri;
        }
        
        /// <summary>
        /// Pastes the generated cropped face back into the original image using seamless blending.
        /// This method transforms the cropped face to match the original image coordinate system and blends
        /// it with the original image using the provided mask for natural-looking results.
        /// </summary>
        /// <param name="imgCrop">The generated cropped face image to paste back</param>
        /// <param name="Mc2o">The transformation matrix from crop coordinates to original image coordinates</param>
        /// <param name="imgOri">The original image to paste the face into</param>
        /// <param name="maskOri">The blending mask that defines the paste-back region</param>
        /// <returns>The final composed image with the generated face seamlessly blended into the original image</returns>
        private Frame PasteBack(Frame imgCrop,
                                Matrix4x4 Mc2o, 
                                Frame imgOri, 
                                Frame maskOri)
        {
            
            int dsize_w = imgOri.width;
            int dsize_h = imgOri.height;
            
            float[,] M = new float[,] {
                { Mc2o.m00, Mc2o.m01, Mc2o.m03 },
                { Mc2o.m10, Mc2o.m11, Mc2o.m13 }
            };
            var warped = FrameUtils.AffineTransformFrame(imgCrop, M, dsize_w, dsize_h);
            
            var result = new Frame(new byte[dsize_w * dsize_h * 3], dsize_w, dsize_h);
            
            for (int i = 0; i < imgOri.width * imgOri.height; i++)
            {
                var rIndex = 3 * i + 0;
                var gIndex = 3 * i + 1;
                var bIndex = 3 * i + 2;

                var maskR = maskOri.data[rIndex] / 255f;
                var maskG = maskOri.data[gIndex] / 255f;
                var maskB = maskOri.data[bIndex] / 255f;
                
                float r = imgOri.data[rIndex] * (1f - maskR) + warped.data[rIndex] * maskR;
                float g = imgOri.data[gIndex] * (1f - maskG) + warped.data[gIndex] * maskG;
                float b = imgOri.data[bIndex] * (1f - maskB) + warped.data[bIndex] * maskB;
                
                result.data[rIndex] = (byte)(r < 0f ? 0f : r > 255f ? 255f : r);
                result.data[gIndex] = (byte)(g < 0f ? 0f : g > 255f ? 255f : g);
                result.data[bIndex] = (byte)(b < 0f ? 0f : b > 255f ? 255f : b);
            }
            return result;
        }

        #endregion

        #region IDisposable Implementation

        /// <summary>
        /// Releases all resources used by the LivePortraitInference instance.
        /// Disposes of all ONNX models and prevents memory leaks.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the LivePortraitInference and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources (ONNX models)
                    _appearanceFeatureExtractor?.Dispose();
                    _motionExtractor?.Dispose();
                    _stitching?.Dispose();
                    _warpingSpade?.Dispose();
                    _faceAnalysis?.Dispose();
                    
                    Logger.LogVerbose("[LivePortraitInference] All models disposed successfully");
                }
                
                _disposed = true;
            }
        }

        /// <summary>
        /// Finalizer for the LivePortraitInference class.
        /// </summary>
        ~LivePortraitInference()
        {
            Dispose(false);
        }

        #endregion
    }
}
