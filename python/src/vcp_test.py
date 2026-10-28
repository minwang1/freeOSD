"""Hardware-independent orchestration for testing and restoring a VCP value."""


def read_vcp_with_fallback(code, direct_read, read_all, logger, progress=None):
    """Read one VCP code, then search a bulk snapshot if its direct read fails."""
    direct_error_text = None
    try:
        value = direct_read()
        logger.info("vcp_read_succeeded code=0x%02X source=single value=%s", code, value)
        return value
    except Exception as direct_error:
        direct_error_text = str(direct_error)
        logger.exception("vcp_single_read_failed code=0x%02X; falling back to full VCP scan", code)
        if progress:
            progress(f"Single read of VCP 0x{code:02X} failed; scanning all VCP codes 0x00–0xFF…")

    try:
        values = read_all()
    except Exception as scan_error:
        logger.exception("vcp_full_scan_failed code=0x%02X", code)
        raise OSError(
            f"Single read of VCP 0x{code:02X} failed ({direct_error_text}); full VCP scan failed ({scan_error})"
        ) from scan_error

    if code in values:
        value = values[code]
        logger.info("vcp_read_succeeded code=0x%02X source=full_scan value=%s", code, value)
        if progress:
            progress(f"Full scan found VCP 0x{code:02X} = {value}.")
        return value

    logger.error("vcp_read_unavailable code=0x%02X source=single_and_full_scan", code)
    if progress:
        progress(f"Full scan could not read VCP 0x{code:02X}.")
    raise OSError(f"VCP 0x{code:02X} could not be read directly or in the full VCP scan: {direct_error_text}")


def run_vcp_value_test(
    code,
    initial_value,
    target_value,
    reacquire,
    write_value,
    read_value,
    wait,
    logger,
    progress=None,
    restore_attempts=5,
    settle_seconds=4.0,
):
    """Set one candidate, verify its readback, restore the initial value, and verify again.

    Write API exceptions are logged, but pass/fail is determined only by the VCP
    values read back after each write. This handles drivers that report an error
    even though the monitor applied the requested value.
    """

    def report(message):
        if progress:
            progress(message)

    def write_and_read(stage, target, attempt):
        report(f"{stage.upper()}: writing VCP 0x{code:02X} = {target}.")
        try:
            record = reacquire()
            try:
                write_value(record, code, target)
                report(f"{stage.upper()}: write command returned; waiting {settle_seconds:g} seconds.")
                logger.info(
                    "vcp_value_write_returned code=0x%02X stage=%s target=%d attempt=%d",
                    code, stage, target, attempt,
                )
            except Exception:
                report(f"{stage.upper()}: write call reported an error; waiting {settle_seconds:g} seconds before reading.")
                logger.exception(
                    "vcp_value_write_reported_error code=0x%02X stage=%s target=%d attempt=%d; checking readback",
                    code, stage, target, attempt,
                )
            wait(settle_seconds)
            report(f"{stage.upper()}: reading VCP 0x{code:02X}.")
            record = reacquire()
            actual = read_value(record, code)
            report(f"{stage.upper()}: read VCP 0x{code:02X} = {actual} (expected {target}).")
            logger.info(
                "vcp_value_readback code=0x%02X stage=%s target=%d actual=%s attempt=%d matched=%s",
                code, stage, target, actual, attempt, actual == target,
            )
            return {"actual": actual, "error": None}
        except Exception as exc:
            report(f"{stage.upper()}: read/check failed: {exc}")
            logger.exception(
                "vcp_value_check_failed code=0x%02X stage=%s target=%d attempt=%d",
                code, stage, target, attempt,
            )
            return {"actual": None, "error": str(exc)}

    logger.info(
        "vcp_value_test_started code=0x%02X initial=%d target=%d",
        code, initial_value, target_value,
    )
    report(f"Test starting value: VCP 0x{code:02X} = {initial_value}.")
    tested = write_and_read("test", target_value, 1)
    restore = {"actual": None, "error": None, "attempts": 0}
    for attempt in range(1, restore_attempts + 1):
        restore = write_and_read("restore", initial_value, attempt)
        restore["attempts"] = attempt
        if restore["actual"] == initial_value:
            break

    test_passed = tested["actual"] == target_value
    restore_passed = restore["actual"] == initial_value
    passed = test_passed and restore_passed
    logger.info(
        "vcp_value_test_finished code=0x%02X initial=%d target=%d test_readback=%s "
        "restore_readback=%s restore_attempts=%d passed=%s",
        code, initial_value, target_value, tested["actual"], restore["actual"],
        restore["attempts"], passed,
    )
    return {
        "target_value": target_value,
        "test_readback": tested["actual"],
        "test_error": tested["error"],
        "test_passed": test_passed,
        "restore_readback": restore["actual"],
        "restore_error": restore["error"],
        "restore_attempts": restore["attempts"],
        "restore_passed": restore_passed,
        "passed": passed,
    }
