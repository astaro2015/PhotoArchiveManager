Assets downloaded by BUILD_ON_CLEAN_WINDOWS.bat before compilation and embedded into the single EXE:
- haarcascade_frontalface_default.xml (OpenCV; quality analyzer)
- haarcascade_eye_tree_eyeglasses.xml (OpenCV; quality analyzer)
- face_detection_yunet_2023mar.onnx (OpenCV Zoo YuNet; people indexing)
- face_recognition_sface_2021dec.onnx (OpenCV Zoo SFace; face embeddings)

The YuNet and SFace ONNX model files are SHA-256 verified by the build script before compilation.
At runtime the embedded models are extracted to Data/Models; original photos are never modified by analysis.
