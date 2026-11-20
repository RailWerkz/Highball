// Highball's build of AMD FidelityFX FSR 2.2.1 (MIT, LICENSE.txt beside this; the ffx_* files are AMD's, unchanged but for the
// #version line taken off each pass, rw_upscaled_output given its format, and rw_prepared_input_color and rw_dilatedDepth declared in their real formats, rgba16f and r32f). Each fsr2_*.comp sets the version, takes these
// options and includes one pass. Used by Highball/World/Fsr2.cs.
//
// 32-bit maths throughout (no fp16); the colour's HDR; motion vectors at the render size, without the jitter; depth reversed
// (infinite: Highball's is the near distance over the distance).
#define FFX_GPU 1
#define FFX_GLSL 1
#define FFX_HALF 0
#define FFX_FSR2_OPTION_UPSAMPLE_SAMPLERS_USE_DATA_HALF 0
#define FFX_FSR2_OPTION_ACCUMULATE_SAMPLERS_USE_DATA_HALF 0
#define FFX_FSR2_OPTION_REPROJECT_SAMPLERS_USE_DATA_HALF 0
#define FFX_FSR2_OPTION_POSTPROCESSLOCKSTATUS_SAMPLERS_USE_DATA_HALF 0
#define FFX_FSR2_OPTION_UPSAMPLE_USE_LANCZOS_TYPE 2
#define FFX_FSR2_OPTION_REPROJECT_USE_LANCZOS_TYPE 0
#define FFX_FSR2_OPTION_HDR_COLOR_INPUT 1
#define FFX_FSR2_OPTION_LOW_RESOLUTION_MOTION_VECTORS 1
#define FFX_FSR2_OPTION_JITTERED_MOTION_VECTORS 0
#define FFX_FSR2_OPTION_INVERTED_DEPTH 1
#define FFX_FSR2_OPTION_APPLY_SHARPENING 1
