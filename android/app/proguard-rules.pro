# UniFFI bindings reach the core through JNA, by reflection.
-keep class com.sun.jna.** { *; }
-keep class * implements com.sun.jna.** { *; }
-keep class uniffi.yuyue_core.** { *; }
# ONNX Runtime JNI (libonnxruntime4j_jni.so) looks up TensorInfo, OrtException,
# OnnxTensor constructors, and related types by exact name and signature.
-keep class ai.onnxruntime.** { *; }
-dontwarn java.awt.**
