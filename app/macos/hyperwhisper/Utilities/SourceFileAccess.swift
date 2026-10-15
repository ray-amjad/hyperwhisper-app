//
//  SourceFileAccess.swift
//  hyperwhisper
//
//  Why a file the user picked (Transcribe File) or named (Local API `file`)
//  cannot be read, told apart by errno (issue #1551).
//
//  Before this, both paths gave the wrong cause: Transcribe File showed
//  Foundation's copy error, which names the recordings folder (the copy's
//  destination), and the Local API answered FILE_NOT_FOUND for a folder or an
//  unenterable parent, and a Full Disk Access hint for a plain POSIX mode 000.
//
//  The split Ray settled (inbox #358): a privacy (TCC) block surfaces as EPERM
//  and is the only case Full Disk Access can fix; EACCES is POSIX permissions —
//  on the file itself when `open` fails, on a folder in the path when `stat`
//  fails.
//

import Darwin
import Foundation

enum SourceFileAccess: Equatable {
    /// A file `open(O_RDONLY)` succeeds on.
    case readable
    /// Nothing is there: missing, a dangling or looping symlink, or a path
    /// component that is not a folder.
    case missing
    /// The path names a folder, not a file.
    case isFolder
    /// `stat` got EACCES: a folder in the path cannot be entered.
    case folderInPathDenied
    /// `open` got EACCES: the file's own permissions forbid reading it.
    case fileDenied
    /// EPERM: macOS privacy protection (TCC) blocked the read. The one case
    /// Full Disk Access can fix.
    case privacyBlocked
    /// Any other errno.
    case unreadable(errno: Int32)

    /// Stat `path`, then open and close it read-only.
    ///
    /// `O_NONBLOCK`, so a FIFO cannot hang the caller in `open`. Nothing is
    /// read; the open is there to get the errno the read would have got.
    static func probe(path: String) -> SourceFileAccess {
        var info = stat()
        guard stat(path, &info) == 0 else {
            return classify(statErrno: errno)
        }
        if (info.st_mode & S_IFMT) == S_IFDIR {
            return .isFolder
        }
        let fd = open(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else {
            return classify(openErrno: errno)
        }
        close(fd)
        return .readable
    }

    /// What a failed `stat` of the path means. A file's own mode cannot make
    /// `stat` fail, so EACCES here is always a folder in the path.
    static func classify(statErrno code: Int32) -> SourceFileAccess {
        switch code {
        case ENOENT, ENOTDIR, ELOOP, ENAMETOOLONG: return .missing
        case EACCES: return .folderInPathDenied
        case EPERM: return .privacyBlocked
        default: return .unreadable(errno: code)
        }
    }

    /// What a failed `open(O_RDONLY)` of a path that `stat` succeeded on means.
    static func classify(openErrno code: Int32) -> SourceFileAccess {
        switch code {
        case ENOENT, ENOTDIR, ELOOP: return .missing
        case EISDIR: return .isFolder
        case EACCES: return .fileDenied
        case EPERM: return .privacyBlocked
        default: return .unreadable(errno: code)
        }
    }
}
