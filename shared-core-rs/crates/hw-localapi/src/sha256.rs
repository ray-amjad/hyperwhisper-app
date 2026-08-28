//! SHA-256 (FIPS 180-4), written out so this crate keeps zero dependencies.
//!
//! It exists for exactly one caller: [`crate::authorize`] hashes the presented
//! and the stored bearer token and compares the two digests in constant time.
//! Do not reach for it as a general-purpose hash — it is not tuned, and its
//! only contract is "same bytes as every other SHA-256".
//!
//! # Why hash at all
//!
//! The three heads did three different things (issue #289): macOS compared the
//! raw UTF-8 bytes with a hand-rolled XOR loop that returns early on a length
//! mismatch, Windows used `FixedTimeEquals` on UTF-8, and Linux used
//! `FixedTimeEquals` on SHA-256 digests. Hashing first is what makes the
//! compare genuinely length-independent: both digests are always 32 bytes, so
//! the loop runs the same number of times whatever the caller sent, and the
//! length of the token stops being observable.
//!
//! # Panic-free
//!
//! No indexing, no slicing, no `unwrap`. Every lookup goes through `get` with a
//! defined fallback, and every buffer is a fixed-size array. `clippy::
//! indexing_slicing` is denied crate-wide, which is what keeps it that way.

/// Round constants — the first 32 bits of the fractional parts of the cube
/// roots of the first 64 primes (FIPS 180-4 §4.2.2).
#[rustfmt::skip]
const K: [u32; 64] = [
    0x428a_2f98, 0x7137_4491, 0xb5c0_fbcf, 0xe9b5_dba5, 0x3956_c25b, 0x59f1_11f1, 0x923f_82a4,
    0xab1c_5ed5, 0xd807_aa98, 0x1283_5b01, 0x2431_85be, 0x550c_7dc3, 0x72be_5d74, 0x80de_b1fe,
    0x9bdc_06a7, 0xc19b_f174, 0xe49b_69c1, 0xefbe_4786, 0x0fc1_9dc6, 0x240c_a1cc, 0x2de9_2c6f,
    0x4a74_84aa, 0x5cb0_a9dc, 0x76f9_88da, 0x983e_5152, 0xa831_c66d, 0xb003_27c8, 0xbf59_7fc7,
    0xc6e0_0bf3, 0xd5a7_9147, 0x06ca_6351, 0x1429_2967, 0x27b7_0a85, 0x2e1b_2138, 0x4d2c_6dfc,
    0x5338_0d13, 0x650a_7354, 0x766a_0abb, 0x81c2_c92e, 0x9272_2c85, 0xa2bf_e8a1, 0xa81a_664b,
    0xc24b_8b70, 0xc76c_51a3, 0xd192_e819, 0xd699_0624, 0xf40e_3585, 0x106a_a070, 0x19a4_c116,
    0x1e37_6c08, 0x2748_774c, 0x34b0_bcb5, 0x391c_0cb3, 0x4ed8_aa4a, 0x5b9c_ca4f, 0x682e_6ff3,
    0x748f_82ee, 0x78a5_636f, 0x84c8_7814, 0x8cc7_0208, 0x90be_fffa, 0xa450_6ceb, 0xbef9_a3f7,
    0xc671_78f2,
];

/// Initial hash value — the first 32 bits of the fractional parts of the square
/// roots of the first 8 primes (FIPS 180-4 §5.3.3).
#[rustfmt::skip]
const H0: [u32; 8] = [
    0x6a09_e667, 0xbb67_ae85, 0x3c6e_f372, 0xa54f_f53a,
    0x510e_527f, 0x9b05_688c, 0x1f83_d9ab, 0x5be0_cd19,
];

/// The SHA-256 digest of `data`.
pub(crate) fn sha256(data: &[u8]) -> [u8; 32] {
    let mut state = H0;

    // Every whole 64-byte block, straight from the input.
    let mut blocks = data.chunks_exact(64);
    for block in blocks.by_ref() {
        compress(&mut state, block);
    }

    // The tail: the remaining 0..63 bytes, then the `0x80` terminator, then
    // zero padding, then the 64-bit big-endian bit length. That is one more
    // block when the remainder leaves room for the 9 trailing bytes, and two
    // when it does not.
    let rest = blocks.remainder();
    let rest_len = rest.len();
    let mut tail = [0u8; 128];
    for (slot, byte) in tail.iter_mut().zip(rest.iter()) {
        *slot = *byte;
    }
    if let Some(slot) = tail.get_mut(rest_len) {
        *slot = 0x80;
    }
    let tail_len: usize = if rest_len + 9 <= 64 { 64 } else { 128 };
    let bit_len = (data.len() as u64).wrapping_mul(8);
    for (slot, byte) in tail
        .iter_mut()
        .skip(tail_len.saturating_sub(8))
        .zip(bit_len.to_be_bytes().iter())
    {
        *slot = *byte;
    }
    for block in tail.get(..tail_len).unwrap_or(&[]).chunks_exact(64) {
        compress(&mut state, block);
    }

    let mut digest = [0u8; 32];
    for (slot, word) in digest.chunks_exact_mut(4).zip(state.iter()) {
        for (byte, source) in slot.iter_mut().zip(word.to_be_bytes().iter()) {
            *byte = *source;
        }
    }
    digest
}

/// One 64-byte block into the state (FIPS 180-4 §6.2.2).
///
/// `block` is always exactly 64 bytes — every caller produces it with
/// `chunks_exact(64)` — but nothing here depends on that: a short block simply
/// reads zeros through `get(..).unwrap_or(0)`, rather than panicking.
fn compress(state: &mut [u32; 8], block: &[u8]) {
    let mut schedule = [0u32; 64];
    for (slot, word) in schedule.iter_mut().zip(block.chunks_exact(4)) {
        let mut value = 0u32;
        for byte in word {
            value = (value << 8) | u32::from(*byte);
        }
        *slot = value;
    }
    for index in 16..64 {
        let at = |offset: usize| schedule.get(offset).copied().unwrap_or(0);
        let w15 = at(index - 15);
        let w2 = at(index - 2);
        let s0 = w15.rotate_right(7) ^ w15.rotate_right(18) ^ (w15 >> 3);
        let s1 = w2.rotate_right(17) ^ w2.rotate_right(19) ^ (w2 >> 10);
        let next = at(index - 16)
            .wrapping_add(s0)
            .wrapping_add(at(index - 7))
            .wrapping_add(s1);
        if let Some(slot) = schedule.get_mut(index) {
            *slot = next;
        }
    }

    let read = |offset: usize| state.get(offset).copied().unwrap_or(0);
    let (mut a, mut b, mut c, mut d) = (read(0), read(1), read(2), read(3));
    let (mut e, mut f, mut g, mut h) = (read(4), read(5), read(6), read(7));

    for (word, constant) in schedule.iter().zip(K.iter()) {
        let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
        let choose = (e & f) ^ ((!e) & g);
        let temp1 = h
            .wrapping_add(s1)
            .wrapping_add(choose)
            .wrapping_add(*constant)
            .wrapping_add(*word);
        let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
        let majority = (a & b) ^ (a & c) ^ (b & c);
        let temp2 = s0.wrapping_add(majority);

        h = g;
        g = f;
        f = e;
        e = d.wrapping_add(temp1);
        d = c;
        c = b;
        b = a;
        a = temp1.wrapping_add(temp2);
    }

    for (slot, delta) in state.iter_mut().zip([a, b, c, d, e, f, g, h].iter()) {
        *slot = slot.wrapping_add(*delta);
    }
}

#[cfg(test)]
mod tests {
    use super::sha256;

    fn hex(digest: [u8; 32]) -> String {
        digest.iter().map(|byte| format!("{byte:02x}")).collect()
    }

    /// The FIPS 180-4 sample vectors, plus the empty string and the two block
    /// boundaries the padding branch turns on (55/56 bytes is where the length
    /// field stops fitting in the same block, 64 is an exact block).
    #[test]
    fn the_published_vectors_match() {
        assert_eq!(
            hex(sha256(b"")),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
        assert_eq!(
            hex(sha256(b"abc")),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        assert_eq!(
            hex(sha256(
                b"abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"
            )),
            "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"
        );
        assert_eq!(
            hex(sha256(&[b'a'; 55])),
            "9f4390f8d30c2dd92ec9f095b65e2b9ae9b0a925a5258e241c9f1e910f734318"
        );
        assert_eq!(
            hex(sha256(&[b'a'; 56])),
            "b35439a4ac6f0948b6d6f9e3c6af0f5f590ce20f1bde7090ef7970686ec6738a"
        );
        assert_eq!(
            hex(sha256(&[b'a'; 64])),
            "ffe054fe7ae0cb6dc65c3af9b61d5209f439851db43d0ba5997337df154668eb"
        );
        assert_eq!(
            hex(sha256(&[b'a'; 1000])),
            "41edece42d63e8d9bf515a9ba6932e1c20cbc9f5a5d134645adb5db1b9737ea3"
        );
    }

    /// A real token-shaped input, so the one thing this module is actually
    /// called with is pinned too.
    #[test]
    fn a_token_shaped_input_matches() {
        assert_eq!(
            hex(sha256(b"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")),
            "0f007385b6f9d4b7eeb2748605afe1a984a0a3bfa3f014d09e2a784ce9e5cd1a"
        );
    }
}
