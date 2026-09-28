#!/bin/sh
set -eu

log() {
    printf '[patch-sign] %s\n' "$*"
}

die() {
    printf '[patch-sign] ERROR: %s\n' "$*" >&2
    exit 1
}

input_apk=${INPUT_APK:-/input/Original.apk}
output_name=${OUTPUT_APK:-Original.prototype-signed.apk}
user_payload_dir=${PAYLOAD_DIR:-/payload}
lemon_payload_dir=${LEMON_PAYLOAD_DIR:-/opt/lemon-payload}
output_dir=${OUTPUT_DIR:-/output}
keystore=${KEYSTORE_PATH:-/keys/otogi-dev.keystore}
key_alias=${KEY_ALIAS:-otogi-dev}
package_name=${PACKAGE_NAME:-}
expected_input_package=${EXPECTED_INPUT_PACKAGE:-jp.co.dmm.dmmgames.kms}
expected_input_signer=${EXPECTED_INPUT_SIGNER_SHA256:-96d97aef80ba009d06c26e7b5ef98fde67ffa20b69b4f57d26771bbb2a224c26}
create_key=${CREATE_KEY:-false}
expected_font_sha256=${FONT_SHA256:-929faeecb6a0bd636b92a921d2d590350b72e301832272302a064f3d5a0ab893}
KS_PASS=${KS_PASS:-android}
KEY_PASS=${KEY_PASS:-$KS_PASS}
export KS_PASS KEY_PASS

[ -n "$package_name" ] || die "PACKAGE_NAME is required for bootstrap initialization"
[ -f "$input_apk" ] || die "input APK not found: $input_apk"
[ "$(basename "$output_name")" = "$output_name" ] || die "OUTPUT_APK must be a filename"
mkdir -p "$output_dir" "$(dirname "$keystore")"

work_dir=$(mktemp -d)
trap 'rm -rf "$work_dir"' EXIT INT TERM

unsigned_apk="$work_dir/unsigned.apk"
renamed_apk="$work_dir/renamed.apk"
aligned_apk="$work_dir/aligned.apk"
signed_apk="$work_dir/signed.apk"
payload_list="$work_dir/payload.list"
archive_list="$work_dir/archive.list"
replace_list="$work_dir/replace.list"
compressed_list="$work_dir/compressed.list"
stored_list="$work_dir/stored.list"
verify_log="$work_dir/verify.txt"
input_verify_log="$work_dir/input-verify.txt"
signature_list="$work_dir/signature.list"
required_list="$work_dir/required.list"
final_list="$work_dir/final.list"
missing_list="$work_dir/missing.list"

log "checking input"
unzip -tqq "$input_apk" >/dev/null || die "input is not a valid ZIP/APK"
input_sha=$(sha256sum "$input_apk" | awk '{print $1}')
input_package=$(aapt dump badging "$input_apk" | sed -n "s/^package: name='\([^']*\)'.*/\1/p")
[ -n "$input_package" ] || die "could not read input package name"
[ "$input_package" = "$expected_input_package" ] \
    || die "unexpected input package: $input_package"
apksigner verify --verbose --print-certs "$input_apk" > "$input_verify_log" \
    || die "input APK signature verification failed"
input_signer=$(sed -n 's/^Signer #1 certificate SHA-256 digest: //p' "$input_verify_log")
[ "$input_signer" = "$expected_input_signer" ] \
    || die "unexpected input signer: ${input_signer:-missing}"
cp "$input_apk" "$unsigned_apk"

if [ -n "$package_name" ]; then
    printf '%s\n' "$package_name" | grep -Eq '^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$' \
        || die "invalid PACKAGE_NAME: $package_name"
    [ "$package_name" != "$input_package" ] || die "PACKAGE_NAME must differ from the input package"

    log "renaming package: $input_package -> $package_name"
    decoded_dir="$work_dir/decoded"
    java -jar /usr/local/lib/apktool.jar d -f -s -o "$decoded_dir" "$input_apk" >/dev/null
    manifest="$decoded_dir/AndroidManifest.xml"
    grep -Fq "package=\"$input_package\"" "$manifest" \
        || die "decoded manifest package does not match aapt"
    sed -i "s/package=\"$input_package\"/package=\"$package_name\"/" "$manifest"
    if grep -Eq '<application[^>]*android:name=' "$manifest"; then
        die "input has a custom Application; bootstrap integration requires review"
    fi
    sed -i 's/<application /<application android:name="org.otogi.patcher.OtogiApplication" android:debuggable="true" /' "$manifest"
    # Preserve the original DEX files; compile the initializer into a separate DEX.
    dex_index=2
    while [ -f "$decoded_dir/classes$dex_index.dex" ]; do
        dex_index=$((dex_index + 1))
    done
    bootstrap_dir="$decoded_dir/smali_classes$dex_index/org/otogi/patcher"
    mkdir -p "$bootstrap_dir"
    cp /opt/OtogiApplication.smali "$bootstrap_dir/"
    java -jar /usr/local/lib/apktool.jar b -o "$renamed_apk" "$decoded_dir" >/dev/null
    unzip -tqq "$renamed_apk" >/dev/null || die "renamed APK is invalid"
    compiled_package=$(aapt dump badging "$renamed_apk" | sed -n "s/^package: name='\([^']*\)'.*/\1/p")
    [ "$compiled_package" = "$package_name" ] || die "apktool did not compile the requested package"

    unsigned_apk="$renamed_apk"
fi

: > "$signature_list"
unzip -Z1 "$unsigned_apk" \
    | grep -Ei '^META-INF/(MANIFEST\.MF|[^/]+\.(SF|RSA|DSA|EC))$' \
    | LC_ALL=C sort -u > "$signature_list" || true
if [ -s "$signature_list" ]; then
    log "removing old JAR signature entries"
    zip -q -d "$unsigned_apk" -@ < "$signature_list"
fi

payload_dir="$work_dir/payload"
mkdir "$payload_dir"
[ ! -d "$lemon_payload_dir" ] || cp -a "$lemon_payload_dir/." "$payload_dir/"
[ ! -d "$user_payload_dir" ] || cp -a "$user_payload_dir/." "$payload_dir/"
mkdir -p "$payload_dir/assets/copyToData/Plugins"
cp /opt/plugin/OtogiTranslate.dll "$payload_dir/assets/copyToData/Plugins/OtogiTranslate.dll"
font_file="$payload_dir/assets/copyToData/Assets/font"
font_gzip="$font_file.gz"
font_md5_file="$font_file.md5"
[ -s "$font_gzip" ] || die "compressed replacement font payload is missing"
gzip -dc "$font_gzip" > "$font_file" \
    || die "replacement font payload could not be decompressed"
rm "$font_gzip"
[ -s "$font_file" ] || die "replacement font payload is missing"
[ "$(sha256sum "$font_file" | awk '{print $1}')" = "$expected_font_sha256" ] \
    || die "replacement font payload has an unexpected SHA-256"
[ -s "$font_md5_file" ] || die "replacement font MD5 sidecar is missing"
[ "$(base64 -d < "$font_md5_file" | od -An -tx1 | tr -d ' \n')" \
    = "$(md5sum "$font_file" | awk '{print $1}')" ] \
    || die "replacement font MD5 sidecar is invalid"

# Keep the source outside the game's disposable Assets cache.
font_source_dir="$payload_dir/assets/copyToData/UserData/OtogiTranslate"
mkdir -p "$font_source_dir"
mv "$font_file" "$font_source_dir/font"
rm "$font_md5_file"

il2cpp_entry=lib/arm64-v8a/libil2cpp.so
unzip -Z1 "$input_apk" | grep -Fxq "$il2cpp_entry" \
    || die "input APK has no ARM64 libil2cpp.so"
unzip -p "$input_apk" "$il2cpp_entry" > "$work_dir/libil2cpp.so"
[ -s "$work_dir/libil2cpp.so" ] || die "could not extract ARM64 libil2cpp.so"
il2cpp_sha512=$(sha512sum "$work_dir/libil2cpp.so" | awk '{print $1}')
mkdir -p "$payload_dir/assets/melonloader/etc/assembly_generation"
{
    printf '[Il2CppAssemblyGenerator]\n'
    printf 'GameAssemblyHash = "%s"\n' "$il2cpp_sha512"
    printf 'UnityVersion = "0.0.0.0"\n'
    printf 'DumperVersion = "0.0.0.0"\n'
    printf 'UnhollowerVersion = "0.0.0.0"\n'
    printf 'OldFiles = [ ]\n'
} > "$payload_dir/assets/melonloader/etc/assembly_generation/Config.cfg"

(
    cd "$payload_dir"
    find . -type f -print | sed 's#^\./##' | LC_ALL=C sort
) > "$payload_list"

payload_count=$(wc -l < "$payload_list" | tr -d ' ')
if [ "$payload_count" -gt 0 ]; then
    if grep -Eq '(^|/)META-INF/' "$payload_list"; then
        die "payload must not contain META-INF signature entries"
    fi

    log "injecting $payload_count payload file(s)"
    unzip -Z1 "$unsigned_apk" | LC_ALL=C sort > "$archive_list"
    comm -12 "$archive_list" "$payload_list" > "$replace_list"
    if [ -s "$replace_list" ]; then
        zip -q -d "$unsigned_apk" -@ < "$replace_list"
    fi

    grep -Ev '^lib/[^/]+/[^/]+\.so$' "$payload_list" > "$compressed_list" || true
    grep -E '^lib/[^/]+/[^/]+\.so$' "$payload_list" > "$stored_list" || true

    if [ -s "$compressed_list" ]; then
        (cd "$payload_dir" && zip -q -9 "$unsigned_apk" -@ < "$compressed_list")
    fi
    if [ -s "$stored_list" ]; then
        (cd "$payload_dir" && zip -q -0 "$unsigned_apk" -@ < "$stored_list")
    fi
else
    log "no payload files; exercising rebuild/sign path only"
fi

log "page-aligning APK"
zipalign -p -f 4 "$unsigned_apk" "$aligned_apk"
zipalign -c -p 4 "$aligned_apk" >/dev/null

if [ ! -f "$keystore" ]; then
    [ "$create_key" = true ] \
        || die "signing key not found; set CREATE_KEY=true for the first build"
    log "creating persistent prototype signing key"
    umask 077
    keytool -genkeypair -keystore "$keystore" -storetype JKS -storepass "$KS_PASS" -keypass "$KEY_PASS" -alias "$key_alias" -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=Otogi Prototype,OU=Local Development,O=Otogi Frontier,C=XX' -noprompt >/dev/null 2>&1
fi

log "signing APK"
apksigner sign --ks "$keystore" --ks-key-alias "$key_alias" --ks-pass env:KS_PASS --key-pass env:KEY_PASS --out "$signed_apk" "$aligned_apk"

log "verifying ZIP, alignment, and signature"
unzip -tqq "$signed_apk" >/dev/null
zipalign -c -p 4 "$signed_apk" >/dev/null
apksigner verify --verbose --print-certs "$signed_apk" > "$verify_log"
final_package=$(aapt dump badging "$signed_apk" | sed -n "s/^package: name='\([^']*\)'.*/\1/p")
expected_package=${package_name:-$input_package}
[ "$final_package" = "$expected_package" ] \
    || die "signed APK package mismatch: expected $expected_package, got $final_package"

if [ -n "$package_name" ]; then
    unzip -Z1 "$input_apk" \
        | grep -Eiv '(^META-INF/(MANIFEST\.MF|[^/]+\.(SF|RSA|DSA|EC))$|^res/)' \
        | LC_ALL=C sort -u > "$required_list"
else
    unzip -Z1 "$input_apk" \
        | grep -Eiv '^META-INF/(MANIFEST\.MF|[^/]+\.(SF|RSA|DSA|EC))$' \
        | LC_ALL=C sort -u > "$required_list"
fi
unzip -Z1 "$signed_apk" | LC_ALL=C sort -u > "$final_list"
comm -23 "$required_list" "$final_list" > "$missing_list"
if [ -s "$missing_list" ]; then
    sed -n '1,20p' "$missing_list" >&2
    die "signed APK lost input entries"
fi
input_resource_count=$(unzip -Z1 "$input_apk" | grep -c '^res/' || true)
final_resource_count=$(unzip -Z1 "$signed_apk" | grep -c '^res/' || true)
[ "$final_resource_count" = "$input_resource_count" ] \
    || die "resource entry count changed: expected $input_resource_count, got $final_resource_count"
comm -23 "$payload_list" "$final_list" > "$missing_list"
if [ -s "$missing_list" ]; then
    sed -n '1,20p' "$missing_list" >&2
    die "signed APK is missing payload entries"
fi

output_apk="$output_dir/$output_name"
output_sha_file="$output_apk.sha256"
output_info="$output_apk.build-info.txt"
cp "$signed_apk" "$output_apk"
cp /opt/plugin/OtogiTranslate.dll "$output_dir/OtogiTranslate.dll"
output_sha=$(sha256sum "$output_apk" | awk '{print $1}')
printf '%s  %s\n' "$output_sha" "$output_name" > "$output_sha_file"

{
    printf 'input=%s\n' "$input_apk"
    printf 'input_sha256=%s\n' "$input_sha"
    printf 'input_package=%s\n' "$input_package"
    printf 'input_signer_sha256=%s\n' "$input_signer"
    printf 'output_package=%s\n' "$final_package"
    printf 'payload_files=%s\n' "$payload_count"
    printf 'output=%s\n' "$output_name"
    printf 'output_sha256=%s\n' "$output_sha"
    printf 'zipalign=verified\n'
    printf 'signature=verified\n'
    printf 'non_resource_input_entries=preserved\n'
    printf 'resource_entries=%s\n' "$final_resource_count"
    printf 'payload_entries=verified\n'
    cat "$verify_log"
} > "$output_info"

cat "$verify_log"
log "output: $output_apk"
log "sha256: $output_sha"
