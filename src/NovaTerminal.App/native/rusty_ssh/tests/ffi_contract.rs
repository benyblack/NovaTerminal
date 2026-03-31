use std::mem::{align_of, size_of};

use rusty_ssh::{
    nova_ssh_close, nova_ssh_poll_event, nova_ssh_resize, nova_ssh_submit_response,
    nova_ssh_write, NovaSshConnectArgs, NovaSshEvent, NOVA_SSH_RESULT_INVALID_ARGUMENT,
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
        nova_ssh_poll_event(std::ptr::null_mut(), &mut event, std::ptr::null_mut(), 0)
    );
    assert_eq!(
        NOVA_SSH_RESULT_INVALID_ARGUMENT,
        nova_ssh_resize(std::ptr::null_mut(), 120, 30)
    );
    assert_eq!(
        NOVA_SSH_RESULT_INVALID_ARGUMENT,
        nova_ssh_write(std::ptr::null_mut(), [1u8].as_ptr(), 1)
    );
    assert_eq!(
        NOVA_SSH_RESULT_INVALID_ARGUMENT,
        nova_ssh_submit_response(std::ptr::null_mut(), 1, br#"{}"#.as_ptr(), 2)
    );
    assert_eq!(
        NOVA_SSH_RESULT_INVALID_ARGUMENT,
        nova_ssh_close(std::ptr::null_mut())
    );
}
