//
//  HyperWhisper-Bridging-Header.h
//  hyperwhisper
//
//  Bridging header to import whisper.cpp C framework
//

#ifndef HyperWhisper_Bridging_Header_h
#define HyperWhisper_Bridging_Header_h

// Import whisper.cpp C interface
#import <whisper/whisper.h>

// Import the HyperWhisper shared Rust core FFI (UniFFI-generated low-level
// header). The generated `hyperwhisper_core.swift` guards `import
// hyperwhisper_coreFFI` behind `#if canImport(...)`; since these symbols are
// brought in via this bridging header instead of a separate module, that guard
// is false and the binding links against the C ABI declared here.
#import "Libraries/hyperwhisper_coreFFI.h"

#endif /* HyperWhisper_Bridging_Header_h */