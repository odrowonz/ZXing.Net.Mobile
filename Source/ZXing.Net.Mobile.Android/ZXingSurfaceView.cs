
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using System.Drawing;
using Android.Graphics;
using Android.Content.PM;
using Android.Hardware;
using System.Threading.Tasks;
using System.Threading;
using Camera = Android.Hardware.Camera;

namespace ZXing.Mobile
{
	// based on https://github.com/xamarin/monodroid-samples/blob/master/ApiDemo/Graphics/CameraPreview.cs
    public class ZXingSurfaceView : SurfaceView, ISurfaceHolderCallback, Android.Hardware.Camera.IPreviewCallback, Android.Hardware.Camera.IAutoFocusCallback, IScannerView
	{
		private const int MIN_FRAME_WIDTH = 240;
		private const int MIN_FRAME_HEIGHT = 240;
		private const int MAX_FRAME_WIDTH = 600;
		private const int MAX_FRAME_HEIGHT = 400;
	
		CancellationTokenSource tokenSource;
		ISurfaceHolder surface_holder;
		Android.Hardware.Camera camera;
		MobileBarcodeScanningOptions Options;
		Action<ZXing.Result> callback;
		Activity activity;
        bool isAnalyzing = false;
        bool wasScanned = false;
        bool isTorchOn = false;
        int cameraId = 0;

        static ManualResetEventSlim _cameraLockEvent = new ManualResetEventSlim(true);

		public ZXingSurfaceView (Activity activity)
			: base (activity)
		{			
			this.activity = activity;

            Init ();
		}

	    protected ZXingSurfaceView(IntPtr javaReference, JniHandleOwnership transfer) 
            : base(javaReference, transfer) 
        {
            Init ();
	    }

        void Init ()
        {
            CheckPermissions ();

            this.surface_holder = Holder;
            this.surface_holder.AddCallback (this);
            this.surface_holder.SetType (SurfaceType.PushBuffers);

            this.tokenSource = new System.Threading.CancellationTokenSource();
        }

		void CheckPermissions()
		{
			var perf = PerformanceCounter.Start ();

            Android.Util.Log.Debug (MobileBarcodeScanner.TAG, "Checking Camera Permissions...");

			if (!PlatformChecks.IsPermissionInManifest (this.Context, Android.Manifest.Permission.Camera)
                || !PlatformChecks.IsPermissionGranted (this.Context, Android.Manifest.Permission.Camera))
			{
				var msg = "ZXing.Net.Mobile requires permission to use the Camera (" + Android.Manifest.Permission.Camera + "), but was not found in your AndroidManifest.xml file.";
				Android.Util.Log.Error ("ZXing.Net.Mobile", msg);

				throw new UnauthorizedAccessException (msg);
			}

			PerformanceCounter.Stop (perf, "CheckPermissions took {0}ms");
		}

	    public void SurfaceCreated (ISurfaceHolder holder)
		{
		}
		
		public void SurfaceChanged (ISurfaceHolder holder, Format format, int w, int h)
		{  
            if (camera == null)
                return;

            var perf = PerformanceCounter.Start ();

            var parameters = camera.GetParameters ();
            parameters.PreviewFormat = ImageFormatType.Nv21;


            var availableResolutions = new List<CameraResolution> ();
            foreach (var sps in parameters.SupportedPreviewSizes) {
                availableResolutions.Add (new CameraResolution {
                    Width = sps.Width,
                    Height = sps.Height
                });
            }

            // Try and get a desired resolution from the options selector
            var resolution = Options.GetResolution (availableResolutions);

            // If the user did not specify a resolution, let's try and find a suitable one
            if (resolution == null) {
                // Loop through all supported sizes
                foreach (var sps in parameters.SupportedPreviewSizes) {

                    // Find one that's >= 640x360 but <= 1000x1000
                    // This will likely pick the *smallest* size in that range, which should be fine
                    if (sps.Width >= 640 && sps.Width <= 1000 && sps.Height >= 360 && sps.Height <= 1000) {
                        resolution = new CameraResolution {
                            Width = sps.Width,
                            Height = sps.Height
                        };
                        break;
                    }
                }
            }

            // Google Glass requires this fix to display the camera output correctly
            if (Build.Model.Contains ("Glass")) {
                resolution = new CameraResolution {
                    Width = 640,
                    Height = 360
                };
                // Glass requires 30fps
                parameters.SetPreviewFpsRange (30000, 30000);
            }

            // Hopefully a resolution was selected at some point
            if (resolution != null) {
                Android.Util.Log.Debug(MobileBarcodeScanner.TAG, "Selected Resolution: " + resolution.Width + "x" + resolution.Height);
                parameters.SetPreviewSize (resolution.Width, resolution.Height);
            }

            camera.SetParameters (parameters);

            SetCameraDisplayOrientation (this.activity);

            camera.SetPreviewDisplay (holder);
            camera.StartPreview ();

            //cameraResolution = new Size(parameters.PreviewSize.Width, parameters.PreviewSize.Height);

            PerformanceCounter.Stop (perf, "SurfaceChanged took {0}ms");

            AutoFocus();
		}
		
		public void SurfaceDestroyed (ISurfaceHolder holder)
		{
			//ShutdownCamera ();
		}


		public byte[] rotateCounterClockwise(byte[] data, int width, int height)
		{
			var rotatedData = new byte[data.Length];
			for (int y = 0; y < height; y++) {
				for (int x = 0; x < width; x++)
					rotatedData[x * height + height - y - 1] = data[x + y * width];
			}
			return rotatedData;
		}
		
        DateTime lastPreviewAnalysis = DateTime.UtcNow;
		BarcodeReader barcodeReader = null;

		Task processingTask;

		public void OnPreviewFrame (byte [] bytes, Android.Hardware.Camera camera)
		{
            if (!isAnalyzing)
                return;
            
			//Check and see if we're still processing a previous frame
			if (processingTask != null && !processingTask.IsCompleted)
				return;
            
			if ((DateTime.UtcNow - lastPreviewAnalysis).TotalMilliseconds < Options.DelayBetweenAnalyzingFrames)
				return;

            // Delay a minimum between scans
            if (wasScanned && ((DateTime.UtcNow - lastPreviewAnalysis).TotalMilliseconds < Options.DelayBetweenContinuousScans))
                return;

            wasScanned = false;

			var cameraParameters = camera.GetParameters();
			var width = cameraParameters.PreviewSize.Width;
			var height = cameraParameters.PreviewSize.Height;
			//var img = new YuvImage(bytes, ImageFormatType.Nv21, cameraParameters.PreviewSize.Width, cameraParameters.PreviewSize.Height, null);	
			lastPreviewAnalysis = DateTime.UtcNow;

			processingTask = Task.Factory.StartNew (() =>
			{
				try
				{

					if (barcodeReader == null)
					{
						barcodeReader = new BarcodeReader (null, null, null, (p, w, h, f) => 
					                                  new PlanarYUVLuminanceSource (p, w, h, 0, 0, w, h, false));
						//new PlanarYUVLuminanceSource(p, w, h, dataRect.Left, dataRect.Top, dataRect.Width(), dataRect.Height(), false))
					
						if (this.Options.TryHarder.HasValue)
							barcodeReader.Options.TryHarder = this.Options.TryHarder.Value;
						if (this.Options.PureBarcode.HasValue)
							barcodeReader.Options.PureBarcode = this.Options.PureBarcode.Value;
						if (!string.IsNullOrEmpty (this.Options.CharacterSet))
							barcodeReader.Options.CharacterSet = this.Options.CharacterSet;
						if (this.Options.TryInverted.HasValue)
							barcodeReader.TryInverted = this.Options.TryInverted.Value;
					
						if (this.Options.PossibleFormats != null && this.Options.PossibleFormats.Count > 0)
						{
							barcodeReader.Options.PossibleFormats = new List<BarcodeFormat> ();
						
							foreach (var pf in this.Options.PossibleFormats)
								barcodeReader.Options.PossibleFormats.Add (pf);
						}
					}

					bool rotate = false;
					int newWidth = width;
					int newHeight = height;

					var cDegrees = getCameraDisplayOrientation(this.activity);

					if (cDegrees == 90 || cDegrees == 270)
					{
						rotate = true;
						newWidth = height;
						newHeight = width;
					}
					
					var start = PerformanceCounter.Start();
					
					if (rotate)
						bytes = rotateCounterClockwise(bytes, width, height);
										
					var result = barcodeReader.Decode (bytes, newWidth, newHeight, RGBLuminanceSource.BitmapFormat.Unknown);

						PerformanceCounter.Stop(start, "Decode Time: {0} ms (width: " + width + ", height: " + height + ", degrees: " + cDegrees + ", rotate: " + rotate + ")");
				
					if (result == null || string.IsNullOrEmpty (result.Text))
						return;
				
                    Android.Util.Log.Debug (MobileBarcodeScanner.TAG, "Barcode Found: " + result.Text);
				
                    wasScanned = true;
					callback (result);
				}
				catch (ReaderException)
				{
                    Android.Util.Log.Debug (MobileBarcodeScanner.TAG, "No barcode Found");
					// ignore this exception; it happens every time there is a failed scan
				
				}
				catch (Exception)
				{
					// TODO: this one is unexpected.. log or otherwise handle it
					throw;
				}

			});
		}
		
		
		public void OnAutoFocus (bool success, Android.Hardware.Camera camera)
		{
            Android.Util.Log.Debug(MobileBarcodeScanner.TAG, "AutoFocused");
			
			Task.Factory.StartNew(() => 
			                                             {
				int slept = 0;
				
				while (!tokenSource.IsCancellationRequested && slept < 2000)
				{
					System.Threading.Thread.Sleep(100);
					slept += 100;
				}
				
				if (!tokenSource.IsCancellationRequested)
					AutoFocus();
			});
		}
		
		public override bool OnTouchEvent (MotionEvent e)
		{
			var r = base.OnTouchEvent(e);
			
			AutoFocus();
			
			return r;
		}
		
        public void AutoFocus ()
        {
            AutoFocus (-1, -1);
        }

        public void AutoFocus(int x, int y)
		{
			if (camera != null)
			{
				if (!tokenSource.IsCancellationRequested)
				{
                    Android.Util.Log.Debug(MobileBarcodeScanner.TAG, "AutoFocus Requested");
                    try {                         
                        camera.AutoFocus(this); 
                    } catch (Exception ex) {
                        Android.Util.Log.Debug (MobileBarcodeScanner.TAG, "AutoFocus Failed: {0}", ex);
                    }
				}
			}
		}
		
		//int cameraDegrees = 0;

		int getCameraDisplayOrientation(Activity context)
		{
			var degrees = 0;

			var display = context.WindowManager.DefaultDisplay;

			var rotation = display.Rotation;

			switch(rotation)
			{
			case SurfaceOrientation.Rotation0:
				degrees = 0;
				break;
			case SurfaceOrientation.Rotation90:
				degrees = 90;
				break;
			case SurfaceOrientation.Rotation180:
				degrees = 180;
				break;
			case SurfaceOrientation.Rotation270:
				degrees = 270;
				break;
			}


			Camera.CameraInfo info = new Camera.CameraInfo();
            
			Camera.GetCameraInfo(cameraId, info);

			int correctedDegrees = (360 + info.Orientation - degrees) % 360;

			return correctedDegrees;
		}

		public void SetCameraDisplayOrientation(Activity context) 
		{
			var degrees = getCameraDisplayOrientation (context);

            Android.Util.Log.Debug (MobileBarcodeScanner.TAG, "Changing Camera Orientation to: " + degrees);
			//cameraDegrees = degrees;

			try { camera.SetDisplayOrientation (degrees); }
			catch (Exception ex) {
                Android.Util.Log.Error (MobileBarcodeScanner.TAG, ex.ToString ());
			}
		}
		
		
		public void ShutdownCamera ()
		{
			tokenSource.Cancel();
			
            var theCamera = camera;
            camera = null;

            // make this asyncronous so that we can return from the view straight away instead of waiting for the camera to release.
            Task.Factory.StartNew(() => {
                try {
                    if (theCamera != null) {
                        try {
                            theCamera.SetPreviewCallback(null);
                            theCamera.StopPreview();
                        } catch (Exception ex) {
                            Android.Util.Log.Error (MobileBarcodeScanner.TAG, ex.ToString ());
                        }
                        theCamera.Release();
                    }
                } catch (Exception e) {
                    Android.Util.Log.Error(MobileBarcodeScanner.TAG, e.ToString());
                } finally {
                    ReleaseExclusiveAccess();
                }
            });
		}
		
		
		private void drawResultPoints(Bitmap barcode, ZXing.Result rawResult) 
		{
			var points = rawResult.ResultPoints;
			
			if (points != null && points.Length > 0)
			{
				var canvas = new Canvas(barcode);
				Paint paint = new Paint();
				paint.Color = Android.Graphics.Color.White;
				paint.StrokeWidth = 3.0f;
				paint.SetStyle(Paint.Style.Stroke);
				
				var border = new RectF(2, 2, barcode.Width - 2, barcode.Height - 2);
				canvas.DrawRect(border, paint);
				
				paint.Color = Android.Graphics.Color.Purple;
				
				if (points.Length == 2) 
				{
					paint.StrokeWidth = 4.0f;
					drawLine(canvas, paint, points[0], points[1]);
				} 
				else if (points.Length == 4 &&
				         (rawResult.BarcodeFormat == BarcodeFormat.UPC_A ||
				 rawResult.BarcodeFormat == BarcodeFormat.EAN_13)) 
				{
					// Hacky special case -- draw two lines, for the barcode and metadata
					drawLine(canvas, paint, points[0], points[1]);
					drawLine(canvas, paint, points[2], points[3]);
				}
				else 
				{
					paint.StrokeWidth = 10.0f;
					
					foreach (ResultPoint point in points)
						canvas.DrawPoint(point.X, point.Y, paint);
				}
			}
		}
		
		private void drawLine(Canvas canvas, Paint paint, ResultPoint a, ResultPoint b) 
		{
			canvas.DrawLine(a.X, a.Y, b.X, b.Y, paint);
		}

		public Size FindBestPreviewSize(Android.Hardware.Camera.Parameters p, Size screenRes)
		{
			var max = p.SupportedPreviewSizes.Count;
			
			var s = p.SupportedPreviewSizes[max - 1];
			
			return new Size(s.Width, s.Height);
		}

        private void GetExclusiveAccess()
        {
            Console.WriteLine ("Getting Camera Exclusive access");
            var result = _cameraLockEvent.Wait(TimeSpan.FromSeconds(10));
            if (!result)
                throw new Exception("Couldn't get exclusive access to the camera");

            _cameraLockEvent.Reset();
            Console.WriteLine ("Got Camera Exclusive access");
        }

        private void ReleaseExclusiveAccess()
        {
            if (_cameraLockEvent.IsSet)
                return;
            
            // release the camera exclusive access allowing it to be used again.
            Console.WriteLine ("Releasing Exclusive access to camera");
            _cameraLockEvent.Set();
        }

        public void StartScanning (Action<Result> scanResultCallback, MobileBarcodeScanningOptions options = null)
        {           
            this.callback = scanResultCallback;
            this.Options = options ?? MobileBarcodeScanningOptions.Default;

            lastPreviewAnalysis = DateTime.UtcNow.AddMilliseconds(this.Options.InitialDelayBeforeAnalyzingFrames);
            isAnalyzing = true;

            Console.WriteLine ("StartScanning");

            CheckPermissions ();

            var perf = PerformanceCounter.Start ();

            GetExclusiveAccess();

            try 
            {
                var version = Build.VERSION.SdkInt;

                if (version >= BuildVersionCodes.Gingerbread)
                {
                    Android.Util.Log.Debug (MobileBarcodeScanner.TAG, "Checking Number of cameras...");

                    var numCameras = Android.Hardware.Camera.NumberOfCameras;
                    var camInfo = new Android.Hardware.Camera.CameraInfo();
                    var found = false;
                    Android.Util.Log.Debug (MobileBarcodeScanner.TAG, "Found " + numCameras + " cameras...");

                    var whichCamera = CameraFacing.Back;

                    if (this.Options.UseFrontCameraIfAvailable.HasValue && this.Options.UseFrontCameraIfAvailable.Value)
                        whichCamera = CameraFacing.Front;

                    for (int i = 0; i < numCameras; i++)
                    {
                        Android.Hardware.Camera.GetCameraInfo(i, camInfo);
                        if (camInfo.Facing == whichCamera)
                        {
                            Android.Util.Log.Debug (MobileBarcodeScanner.TAG, "Found " + whichCamera + " Camera, opening...");
                            camera = Android.Hardware.Camera.Open(i);
							cameraId = i;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        Android.Util.Log.Debug(MobileBarcodeScanner.TAG, "Finding " + whichCamera + " camera failed, opening camera 0...");
                        camera = Android.Hardware.Camera.Open(0);
                        cameraId = 0;
                    }
                }
                else
                {
                    camera = Android.Hardware.Camera.Open();
                }

                if (camera == null)
                    Android.Util.Log.Debug(MobileBarcodeScanner.TAG, "Camera is null :(");

                camera.SetPreviewCallback (this);

            } catch (Exception ex) {
                ShutdownCamera ();

                Console.WriteLine("Setup Error: " + ex);
            }

            PerformanceCounter.Stop (perf, "SurfaceCreated took {0}ms");
        }

        public void StopScanning ()
        {
            isAnalyzing = false;
            ShutdownCamera ();
        }

        public void PauseAnalysis ()
        {
            isAnalyzing = false;
        }

        public void ResumeAnalysis ()
        {
            isAnalyzing = true;
        }

        public void Torch (bool on)
        {
            if (!this.Context.PackageManager.HasSystemFeature(PackageManager.FeatureCameraFlash))
            {
                Android.Util.Log.Info(MobileBarcodeScanner.TAG, "Flash not supported on this device");
                return;
            }

            if (!PlatformChecks.IsPermissionInManifest (this.Context, Android.Manifest.Permission.Flashlight)
                || !PlatformChecks.IsPermissionGranted (this.Context, Android.Manifest.Permission.Flashlight))
            {
                var msg = "ZXing.Net.Mobile requires permission to use the Flash (" + Android.Manifest.Permission.Flashlight + "), but was not found in your AndroidManifest.xml file.";
                Android.Util.Log.Error (MobileBarcodeScanner.TAG, msg);

                throw new UnauthorizedAccessException (msg);
            }

            if (camera == null)
            {
                Android.Util.Log.Info(MobileBarcodeScanner.TAG, "NULL Camera");
                return;
            }

            var p = camera.GetParameters();
            var supportedFlashModes = p.SupportedFlashModes;

            if (supportedFlashModes == null)
                supportedFlashModes = new List<string>();

            var flashMode=  string.Empty;

            if (on)
            {
                if (supportedFlashModes.Contains(Android.Hardware.Camera.Parameters.FlashModeTorch))
                    flashMode = Android.Hardware.Camera.Parameters.FlashModeTorch;
                else if (supportedFlashModes.Contains(Android.Hardware.Camera.Parameters.FlashModeOn))
                    flashMode = Android.Hardware.Camera.Parameters.FlashModeOn;
                isTorchOn = true;
            }
            else 
            {
                if ( supportedFlashModes.Contains(Android.Hardware.Camera.Parameters.FlashModeOff))
                    flashMode = Android.Hardware.Camera.Parameters.FlashModeOff;
                isTorchOn = false;
            }

            if (!string.IsNullOrEmpty(flashMode))
            {
                p.FlashMode = flashMode;
                camera.SetParameters(p);
            }
        }

        public void ToggleTorch ()
        {
            Torch (!isTorchOn);
        }
        public MobileBarcodeScanningOptions ScanningOptions {
            get { return Options; }
        }
        public bool IsTorchOn {
            get { return isTorchOn; }
        }
        public bool IsAnalyzing {
            get { return isAnalyzing; }
        }

        bool? hasTorch = null;

        public bool HasTorch {
            get {
                if (hasTorch.HasValue)
                    return hasTorch.Value;  
                
                var p = camera.GetParameters();
                var supportedFlashModes = p.SupportedFlashModes;

                if (supportedFlashModes != null
                    && (supportedFlashModes.Contains (Camera.Parameters.FlashModeTorch)
                        || supportedFlashModes.Contains (Camera.Parameters.FlashModeOn)))
                    hasTorch = true;

                return hasTorch.Value;
            }
        }
	}
}

