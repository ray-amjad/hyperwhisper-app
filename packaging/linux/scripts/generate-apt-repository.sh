#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/../../.." && pwd)"
DEB_PATH=""
OUTPUT_DIR="$REPO_ROOT/artifacts/apt-repository"
SUITE="stable"
CODENAME="stable"
ORIGIN="HyperWhisper"
LABEL="HyperWhisper Linux"
SIGNING_KEY_FILE=""

usage() {
    cat >&2 <<'EOF'
Usage: generate-apt-repository.sh --deb PACKAGE.deb [options]

Options:
  --output-dir DIR          New or empty output directory
  --suite NAME              Distribution suite (default: stable)
  --codename NAME           Distribution codename (default: stable)
  --origin TEXT             Release Origin field
  --label TEXT              Release Label field
  --signing-key-file FILE   Armored/private GPG key; omit for unsigned metadata

SOURCE_DATE_EPOCH controls deterministic metadata timestamps (default: 0).
EOF
}

while (($# > 0)); do
    case "$1" in
        --deb)
            DEB_PATH="${2:-}"
            shift 2
            ;;
        --output-dir)
            OUTPUT_DIR="${2:-}"
            shift 2
            ;;
        --suite)
            SUITE="${2:-}"
            shift 2
            ;;
        --codename)
            CODENAME="${2:-}"
            shift 2
            ;;
        --origin)
            ORIGIN="${2:-}"
            shift 2
            ;;
        --label)
            LABEL="${2:-}"
            shift 2
            ;;
        --signing-key-file)
            SIGNING_KEY_FILE="${2:-}"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            usage
            echo "Unknown argument: $1" >&2
            exit 2
            ;;
    esac
done

if [[ ! -f "$DEB_PATH" ]]; then
    echo "--deb must name an existing Debian package." >&2
    exit 2
fi
if [[ ! "$SUITE" =~ ^[a-z0-9][a-z0-9._-]*$ ]] \
    || [[ ! "$CODENAME" =~ ^[a-z0-9][a-z0-9._-]*$ ]]; then
    echo "Suite and codename must contain only lowercase letters, digits, dot, underscore, or dash." >&2
    exit 2
fi
if [[ -n "$SIGNING_KEY_FILE" && ! -f "$SIGNING_KEY_FILE" ]]; then
    echo "Signing key file does not exist." >&2
    exit 2
fi
if ! command -v dpkg-scanpackages >/dev/null 2>&1; then
    echo "dpkg-scanpackages is required (package: dpkg-dev)." >&2
    exit 2
fi

OUTPUT_PARENT="$(dirname -- "$OUTPUT_DIR")"
OUTPUT_BASENAME="$(basename -- "$OUTPUT_DIR")"
if [[ "$OUTPUT_BASENAME" == "." || "$OUTPUT_BASENAME" == ".." ]] \
    || [[ ! -d "$OUTPUT_PARENT" || -L "$OUTPUT_PARENT" ]]; then
    echo "Output parent must be an existing real directory." >&2
    exit 2
fi
OUTPUT_PARENT="$(cd -- "$OUTPUT_PARENT" && pwd -P)"
OUTPUT_DIR="$OUTPUT_PARENT/$OUTPUT_BASENAME"
case "$OUTPUT_DIR" in
    /|/root|/home|"$REPO_ROOT")
        echo "Refusing unsafe output directory: $OUTPUT_DIR" >&2
        exit 2
        ;;
esac
if [[ -L "$OUTPUT_DIR" ]]; then
    echo "Refusing symlink output directory: $OUTPUT_DIR" >&2
    exit 2
fi
if [[ -d "$OUTPUT_DIR" && -n "$(find "$OUTPUT_DIR" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
    echo "Output directory must be new or empty: $OUTPUT_DIR" >&2
    exit 2
fi

SOURCE_EPOCH="${SOURCE_DATE_EPOCH:-0}"
if [[ ! "$SOURCE_EPOCH" =~ ^[0-9]+$ ]]; then
    echo "SOURCE_DATE_EPOCH must be a non-negative integer." >&2
    exit 2
fi

STAGING_ROOT="$(mktemp -d -t hyperwhisper-apt.XXXXXXXX)"
GNUPG_TEMP=""
cleanup() {
    rm -rf -- "$STAGING_ROOT"
    if [[ -n "$GNUPG_TEMP" ]]; then
        rm -rf -- "$GNUPG_TEMP"
    fi
}
trap cleanup EXIT

POOL_DIR="$STAGING_ROOT/pool/main/h/hyperwhisper"
BINARY_DIR="$STAGING_ROOT/dists/$SUITE/main/binary-amd64"
install -d -m 0755 "$POOL_DIR" "$BINARY_DIR"
PACKAGE_NAME="$(basename -- "$DEB_PATH")"
install -m 0644 "$DEB_PATH" "$POOL_DIR/$PACKAGE_NAME"

(
    cd "$STAGING_ROOT"
    dpkg-scanpackages --arch amd64 pool /dev/null > \
        "dists/$SUITE/main/binary-amd64/Packages"
)
gzip -n -9 -c "$BINARY_DIR/Packages" > "$BINARY_DIR/Packages.gz"

RELEASE_PATH="$STAGING_ROOT/dists/$SUITE/Release"
RELEASE_DATE="$(date --utc --date="@$SOURCE_EPOCH" '+%a, %d %b %Y %H:%M:%S +0000')"
{
    printf 'Origin: %s\n' "$ORIGIN"
    printf 'Label: %s\n' "$LABEL"
    printf 'Suite: %s\n' "$SUITE"
    printf 'Codename: %s\n' "$CODENAME"
    printf 'Date: %s\n' "$RELEASE_DATE"
    printf 'Architectures: amd64\n'
    printf 'Components: main\n'
    printf 'Description: HyperWhisper Linux package repository\n'
    for algorithm in MD5Sum SHA1 SHA256; do
        printf '%s:\n' "$algorithm"
        for metadata in main/binary-amd64/Packages main/binary-amd64/Packages.gz; do
            case "$algorithm" in
                MD5Sum) digest="$(md5sum "$STAGING_ROOT/dists/$SUITE/$metadata" | awk '{print $1}')" ;;
                SHA1) digest="$(sha1sum "$STAGING_ROOT/dists/$SUITE/$metadata" | awk '{print $1}')" ;;
                SHA256) digest="$(sha256sum "$STAGING_ROOT/dists/$SUITE/$metadata" | awk '{print $1}')" ;;
            esac
            size="$(stat --format='%s' "$STAGING_ROOT/dists/$SUITE/$metadata")"
            printf ' %s %16s %s\n' "$digest" "$size" "$metadata"
        done
    done
} > "$RELEASE_PATH"

if [[ -n "$SIGNING_KEY_FILE" ]]; then
    if ! command -v gpg >/dev/null 2>&1; then
        echo "gpg is required when --signing-key-file is supplied." >&2
        exit 2
    fi
    GNUPG_TEMP="$(mktemp -d -t hyperwhisper-gnupg.XXXXXXXX)"
    chmod 0700 "$GNUPG_TEMP"
    gpg --batch --quiet --homedir "$GNUPG_TEMP" --import "$SIGNING_KEY_FILE"
    KEY_FINGERPRINT="$(
        gpg --batch --homedir "$GNUPG_TEMP" --with-colons --list-secret-keys \
            | awk -F: '$1 == "fpr" { print $10; exit }'
    )"
    if [[ -z "$KEY_FINGERPRINT" ]]; then
        echo "The supplied file contains no usable private signing key." >&2
        exit 2
    fi
    gpg --batch --quiet --homedir "$GNUPG_TEMP" --armor \
        --export "$KEY_FINGERPRINT" > "$STAGING_ROOT/hyperwhisper-archive-keyring.asc"
    gpg --batch --yes --homedir "$GNUPG_TEMP" --local-user "$KEY_FINGERPRINT" \
        --armor --detach-sign --output "$RELEASE_PATH.gpg" "$RELEASE_PATH"
    gpg --batch --yes --homedir "$GNUPG_TEMP" --local-user "$KEY_FINGERPRINT" \
        --armor --clearsign --output "$STAGING_ROOT/dists/$SUITE/InRelease" "$RELEASE_PATH"
fi

install -d -m 0755 "$OUTPUT_DIR"
cp -a -- "$STAGING_ROOT/." "$OUTPUT_DIR/"
find "$OUTPUT_DIR" -type d -exec chmod 0755 {} +
find "$OUTPUT_DIR" -type f -exec chmod 0644 {} +
printf '%s\n' "$OUTPUT_DIR"
