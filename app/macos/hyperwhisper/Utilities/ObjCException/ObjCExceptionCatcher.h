//
//  ObjCExceptionCatcher.h
//  hyperwhisper
//
//  Swift cannot catch an Objective-C exception: one raised inside an AVFoundation
//  call (`AVAudioInputNode.installTap` on a format mismatch, issue #2577) crashes
//  the app. This runs a block inside `@try` and hands the exception back as an
//  `NSError`, which Swift sees as a `throws`. Use it through `ObjCExceptionGuard`.
//

#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

/// The `NSError` domain of a caught exception. `userInfo` holds the exception's
/// `name` and `reason` under `ObjCExceptionNameKey` and `NSLocalizedFailureReasonErrorKey`.
extern NSErrorDomain const ObjCExceptionErrorDomain;
extern NSString *const ObjCExceptionNameKey;

@interface ObjCExceptionCatcher : NSObject

/// Run `block`. Returns `NO` and fills `error` when it raised an Objective-C exception.
+ (BOOL)catchException:(NS_NOESCAPE void (^)(void))block error:(NSError *_Nullable *_Nullable)error;

@end

NS_ASSUME_NONNULL_END
