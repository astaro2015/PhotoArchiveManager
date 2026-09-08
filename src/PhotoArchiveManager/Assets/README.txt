Assets downloaded by BUILD_ON_CLEAN_WINDOWS.bat before compilation and embedded into the single EXE:
- face_detection_yunet_2023mar.onnx (OpenCV Zoo YuNet; people indexing + Quality Score face analysis)
- face_recognition_sface_2021dec.onnx (OpenCV Zoo SFace; face embeddings)

Both ONNX model files are SHA-256 verified by the build script before compilation.
At runtime the embedded models are extracted to Data/Models and verified byte-for-byte against the embedded copy; stale/corrupt local copies are replaced automatically. Original photos are never modified by analysis.
