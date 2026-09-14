#!/usr/bin/env bash

set -Eeuo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
readonly REPO_ROOT
readonly TOOL="$REPO_ROOT/u03-modem-switch"
readonly FIXTURES="$REPO_ROOT/tests/fixtures"

fail() {
    printf 'FAIL: %s\n' "$*" >&2
    exit 1
}

assert_contains() {
    local haystack="$1" needle="$2"
    [[ "$haystack" == *"$needle"* ]] || fail "expected output to contain: $needle"
}

[[ "$($TOOL --version)" == "0.1.0" ]] || fail "unexpected version"
$TOOL --help >/dev/null

for product in 1484 1483 1481 0016; do
    output=$(U03_SYSFS_USB_ROOT="$FIXTURES/$product" "$TOOL" --status 2>&1)
    assert_contains "$output" "USB ID: 19d2:$product"
done

output=$(U03_SYSFS_USB_ROOT="$FIXTURES/1481" "$TOOL" --status 2>&1)
assert_contains "$output" "Mode:   modem"
assert_contains "$output" "/dev/ttyUSB0"
assert_contains "$output" "/dev/ttyUSB1"

output=$(U03_SYSFS_USB_ROOT="$FIXTURES/1481" "$TOOL" --yes 2>&1)
assert_contains "$output" "already in modem mode"

if U03_SYSFS_USB_ROOT="$FIXTURES/empty" "$TOOL" --status >/dev/null 2>&1; then
    fail "empty fixture should not detect a device"
fi

printf 'All smoke tests passed.\n'
