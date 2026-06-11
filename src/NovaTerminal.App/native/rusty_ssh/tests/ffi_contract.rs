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
