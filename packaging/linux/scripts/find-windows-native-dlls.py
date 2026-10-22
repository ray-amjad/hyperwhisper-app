#!/usr/bin/env python3
"""List Windows NATIVE DLLs under one or more directories.

A self-contained .NET publish carries hundreds of managed assemblies that are
also named *.dll, and the Linux app needs every one of them. NuGet packages can
additionally drop Windows native binaries (for example onnxruntime.dll) into
the publish root, which a Linux package must not ship.

Both kinds are PE images, so the file name cannot tell them apart. A managed
assembly is a PE image whose CLR runtime header (data directory 14) is present;
a native DLL has none. Only *.dll files that parse as PE images WITHOUT a CLR
header are printed, NUL-separated (-0) or one per line. A *.dll that is not a
PE image at all is left alone: it is not a Windows binary.
"""

import os
import struct
import sys

CLR_DIRECTORY_INDEX = 14


def is_windows_native_pe(path: str) -> bool:
    try:
        with open(path, "rb") as handle:
            dos = handle.read(64)
            if len(dos) < 64 or dos[:2] != b"MZ":
                return False
            (pe_offset,) = struct.unpack_from("<I", dos, 0x3C)
            handle.seek(pe_offset)
            # Signature (4) + COFF file header (20) + optional header magic (2).
            header = handle.read(26)
            if len(header) < 26 or header[:4] != b"PE\0\0":
                return False
            (optional_size,) = struct.unpack_from("<H", header, 4 + 16)
            (magic,) = struct.unpack_from("<H", header, 24)
            if magic == 0x10B:  # PE32
                count_offset, directories_offset = 92, 96
            elif magic == 0x20B:  # PE32+
                count_offset, directories_offset = 108, 112
            else:
                return False
            handle.seek(pe_offset + 24)
            optional = handle.read(optional_size)
            if len(optional) < directories_offset:
                # Too short to hold any data directory: no CLR header.
                return True
            (directory_count,) = struct.unpack_from("<I", optional, count_offset)
            clr_offset = directories_offset + CLR_DIRECTORY_INDEX * 8
            if directory_count <= CLR_DIRECTORY_INDEX or len(optional) < clr_offset + 8:
                return True
            clr_rva, clr_size = struct.unpack_from("<II", optional, clr_offset)
            return clr_rva == 0 or clr_size == 0
    except OSError:
        return False


def main(arguments: list[str]) -> int:
    separator = "\n"
    if arguments and arguments[0] == "-0":
        separator = "\0"
        arguments = arguments[1:]
    if not arguments:
        print("Usage: find-windows-native-dlls.py [-0] DIR [DIR ...]", file=sys.stderr)
        return 2
    found = []
    for root in arguments:
        if not os.path.isdir(root):
            print(f"Not a directory: {root}", file=sys.stderr)
            return 2
        for directory, _, files in os.walk(root):
            for name in files:
                path = os.path.join(directory, name)
                if (
                    name.lower().endswith(".dll")
                    and os.path.isfile(path)
                    and not os.path.islink(path)
                    and is_windows_native_pe(path)
                ):
                    found.append(path)
    for path in sorted(found):
        sys.stdout.write(path + separator)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
