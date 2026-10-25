//
//  ObjCExceptionCatcher.m
//  hyperwhisper
//
//  See ObjCExceptionCatcher.h.
//

#import "ObjCExceptionCatcher.h"

NSErrorDomain const ObjCExceptionErrorDomain = @"HyperWhisper.ObjCException";
NSString *const ObjCExceptionNameKey = @"ObjCExceptionName";

@implementation ObjCExceptionCatcher

+ (BOOL)catchException:(NS_NOESCAPE void (^)(void))block error:(NSError *_Nullable *_Nullable)error {
    @try {
        block();
        return YES;
    } @catch (NSException *exception) {
        if (error) {
            NSMutableDictionary<NSErrorUserInfoKey, id> *info = [NSMutableDictionary dictionary];
            info[ObjCExceptionNameKey] = exception.name;
            if (exception.reason) {
                info[NSLocalizedFailureReasonErrorKey] = exception.reason;
            }
            *error = [NSError errorWithDomain:ObjCExceptionErrorDomain code:1 userInfo:info];
        }
        return NO;
    }
}

@end
