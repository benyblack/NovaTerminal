use std::ffi::CString;
use std::mem::{align_of, size_of};

use rusty_ssh::{
    nova_ssh_close, nova_ssh_poll_event, nova_ssh_resize, nova_ssh_sftp_list_directory,
    nova_ssh_string_free, nova_ssh_submit_response, nova_ssh_write, NovaSshConnectArgs,
    NovaSshEvent, NOVA_SSH_RESULT_INVALID_ARGUMENT,
};

#[test]
fn ffi_struct_layout_stays_stable() {
    assert_eq!(16, size_of::<NovaSshEvent>());
    assert_eq!(4, align_of::<NovaSshEvent>());

    assert!(size_of::<NovaSshConnectArgs>() >= 32);
    assert!(align_of::<NovaSshConnectArgs>() >= align_of::<usize>());
}

#[test]
fn invalid_handles_are_rejected_cleanly() {
    let mut event = NovaSshEvent::default();

    assert_eq!(
        NOVA_SSH_RESULT_INVALID_ARGUMENT,
        nova_ssh_poll_event(0, &mut event, std::ptr::null_mut(), 0)
    );
    assert_eq!(
        NOVA_SSH_RESULT_INVALID_ARGUMENT,
        nova_ssh_resize(0, 120, 30)
    );
    assert_eq!(
        NOVA_SSH_RESULT_INVALID_ARGUMENT,
        nova_ssh_write(0, [1u8].as_ptr(), 1)
    );
    assert_eq!(
        NOVA_SSH_RESULT_INVALID_ARGUMENT,
        nova_ssh_submit_response(0, 1, br#"{}"#.as_ptr(), 2)
    );
    assert_eq!(
        NOVA_SSH_RESULT_INVALID_ARGUMENT,
        nova_ssh_close(0)
    );
}

#[test]
fn malformed_json_is_rejected_without_panic() {
    let bad = CString::new("{ this is not valid json ").unwrap();
    let mut response: *mut std::os::raw::c_char = std::ptr::null_mut();
    let rc = nova_ssh_sftp_list_directory(bad.as_ptr(), &mut response);
    assert_ne!(rc, 0, "malformed JSON must not report success");
    if !response.is_null() {
        nova_ssh_string_free(response);
    }
}

#[test]
fn invalid_utf8_bytes_are_rejected_without_panic() {
    // #121: the abuse suite covered null pointers, double-close, use-after-free and malformed JSON, but
    // never invalid *encoding*. That is the one gap with history behind it: #152 was a real bug where
    // the DllImport ANSI default silently mangled non-ASCII cmd/cwd/args into U+FFFD, which is why every
    // string parameter now carries [MarshalAs(UnmanagedType.LPUTF8Str)] and why CA2101 is suppressed
    // there rather than "fixed".
    //
    // A managed caller cannot easily produce these bytes, but a caller in any other language can, and
    // the boundary is public. The contract to pin is that `CStr::to_str` failing is handled as a
    // rejection rather than an unwrap across the FFI boundary.
    //
    // 0x80 is a UTF-8 continuation byte with no leader; 0xC3 0x28 is a two-byte sequence whose
    // continuation byte is invalid; 0xED 0xA0 0x80 is a surrogate half, which is well-formed CESU-8 and
    // illegal UTF-8 — the case a naive length-only validator lets through.
    for bad in [
        &b"\x80"[..],
        &b"\xC3\x28"[..],
        &b"\xED\xA0\x80"[..],
        &b"{\"path\":\"/\xFF\xFE\"}"[..],
        &b"\xF4\x90\x80\x80"[..], // beyond U+10FFFF
    ] {
        let payload = CString::new(bad).expect("test bytes must not contain an interior NUL");
        let mut response: *mut std::os::raw::c_char = std::ptr::null_mut();
        let rc = nova_ssh_sftp_list_directory(payload.as_ptr(), &mut response);

        assert_eq!(
            NOVA_SSH_RESULT_INVALID_ARGUMENT, rc,
            "invalid UTF-8 must be rejected as invalid-argument: {bad:?}"
        );

        // The return code alone does not prove *why* it was rejected: malformed JSON returns the same
        // code, so `assert_ne!(rc, 0)` would still pass if `to_str()` were swapped for
        // `to_string_lossy()` — the mangled U+FFFD text would simply fail to parse as JSON instead.
        // The message is what distinguishes the two paths, so assert on that.
        assert!(!response.is_null(), "a rejection must still produce a response body");
        let message = unsafe { std::ffi::CStr::from_ptr(response) }
            .to_string_lossy()
            .into_owned();
        nova_ssh_string_free(response);

        assert!(
            message.contains("non-UTF8"),
            "expected the encoding-rejection path, got: {message}"
        );
    }
}

#[test]
fn oversized_json_is_rejected_without_panic() {
    // Syntactically-valid but semantically-bogus, very large payload.
    let big = format!(r#"{{"junk":"{}"}}"#, "A".repeat(2_000_000));
    let payload = CString::new(big).unwrap();
    let mut response: *mut std::os::raw::c_char = std::ptr::null_mut();
    let rc = nova_ssh_sftp_list_directory(payload.as_ptr(), &mut response);
    assert_ne!(rc, 0, "bogus oversized JSON must not report success");
    if !response.is_null() {
        nova_ssh_string_free(response);
    }
}
