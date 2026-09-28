FROM debian:bookworm-slim@sha256:abd67ffcfa541b485a3dff59865ab629aa048a6c613e639d36e7456b0b229241

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        apksigner \
        aapt \
        ca-certificates \
        coreutils \
        curl \
        gzip \
        openjdk-17-jre-headless \
        mono-mcs \
        libmono-system-runtime-serialization4.0-cil \
        unzip \
        zip \
        zipalign \
    && rm -rf /var/lib/apt/lists/* \
    && curl -fsSL -o /usr/local/lib/apktool.jar \
        https://github.com/iBotPeaches/Apktool/releases/download/v3.0.3/apktool_3.0.3.jar \
    && echo 'dbf930b076c6b9be08d57c449cacefc3bdd6b71ebd59b3066fc0e1f5b14f9423  /usr/local/lib/apktool.jar' \
        | sha256sum -c -

RUN mkdir -p \
        /opt/lemon-payload/assets/bin/Data/Managed/etc \
        /opt/lemon-payload/assets/copyToData/Assets \
        /opt/lemon-payload/assets/melonloader/etc/assembly_generation/managed \
        /opt/lemon-payload/assets/melonloader/etc/assembly_generation/unity \
        /opt/lemon-payload/assets/melonloader/etc/managed \
        /opt/lemon-payload/assets/melonloader/etc/support \
        /opt/lemon-payload/lib/arm64-v8a \
        /tmp/lemon \
    && curl -fsSL -o /tmp/installer_deps.zip \
        https://github.com/LemonLoader/MelonLoader_057/releases/download/0.2.0.1/installer_deps_0.2.0.zip \
    && echo '61bbbd3d9bdf65c22342300c83a03eb26a1e520c61757ab42179789fa7c06047  /tmp/installer_deps.zip' \
        | sha256sum -c - \
    && unzip -q /tmp/installer_deps.zip -d /tmp/lemon \
    && cp /tmp/lemon/core/*.dll /opt/lemon-payload/assets/melonloader/etc/ \
    && cp /tmp/lemon/managed/*.dll /tmp/lemon/mono/bcl/*.dll \
        /opt/lemon-payload/assets/melonloader/etc/managed/ \
    && cp /tmp/lemon/support_modules/*.dll /opt/lemon-payload/assets/melonloader/etc/support/ \
    && cp /tmp/lemon/assembly_generation/*.dll \
        /opt/lemon-payload/assets/melonloader/etc/assembly_generation/managed/ \
    && cp /tmp/lemon/native/*.so /opt/lemon-payload/lib/arm64-v8a/ \
    && curl -fsSL -o /tmp/installer.apk \
        https://github.com/LemonLoader/MelonLoader_057/releases/download/0.2.0.1/com.melonloader.installer_signed.apk \
    && echo '012074928e648f6c483a450d1365c19bf353d8a15ff5968320a2363ce552cc65  /tmp/installer.apk' \
        | sha256sum -c - \
    && unzip -p /tmp/installer.apk assets/il2cpp_etc.zip > /tmp/il2cpp_etc.zip \
    && echo '4d61cd9823b00878f075a115d6f1c3f9eed46a08e3378c4aee17ba7aef3cc7d5  /tmp/il2cpp_etc.zip' \
        | sha256sum -c - \
    && unzip -q /tmp/il2cpp_etc.zip -d /tmp/il2cpp \
    && cp -a /tmp/il2cpp/etc/. /opt/lemon-payload/assets/bin/Data/Managed/etc/ \
    && curl -fsSL -o /tmp/Managed.zip \
        https://github.com/LavaGang/MelonLoader.UnityDependencies/releases/download/2022.3.62/Managed.zip \
    && echo 'e56a9f20544d4b79560da04bf7745377ec52a3406a62f744b3b41e256e34caa6  /tmp/Managed.zip' \
        | sha256sum -c - \
    && unzip -q /tmp/Managed.zip \
        -d /opt/lemon-payload/assets/melonloader/etc/assembly_generation/unity \
    && printf 'enabled\n' > /opt/lemon-payload/assets/copyToData/isEmulator.txt \
    && rm -rf /tmp/lemon /tmp/il2cpp /tmp/*.zip /tmp/installer.apk

COPY plugin/OtogiTranslate.cs plugin/RuntimeTranslation.cs plugin/OtogiCgUnlock.cs /tmp/plugin/
RUN mkdir -p /opt/plugin \
    && mcs -target:exe -define:SELF_TEST -langversion:7.2 \
        -out:/tmp/plugin/OtogiTranslate.SelfTest.exe \
        -r:/opt/lemon-payload/assets/melonloader/etc/managed/Newtonsoft.Json.dll \
        /tmp/plugin/OtogiTranslate.cs /tmp/plugin/RuntimeTranslation.cs \
    && mcs -target:exe -define:SELF_TEST -langversion:7.2 \
        -out:/tmp/plugin/OtogiCgUnlock.SelfTest.exe \
        -r:/opt/lemon-payload/assets/melonloader/etc/managed/Newtonsoft.Json.dll \
        /tmp/plugin/OtogiCgUnlock.cs \
    && cp /opt/lemon-payload/assets/melonloader/etc/managed/Newtonsoft.Json.dll /tmp/plugin/ \
    && mono /tmp/plugin/OtogiTranslate.SelfTest.exe \
    && mono /tmp/plugin/OtogiCgUnlock.SelfTest.exe \
    && mcs -target:library -langversion:7.2 \
        -out:/opt/plugin/OtogiTranslate.dll \
        -r:/opt/lemon-payload/assets/melonloader/etc/MelonLoader.dll \
        -r:/opt/lemon-payload/assets/melonloader/etc/managed/Newtonsoft.Json.dll \
        /tmp/plugin/OtogiTranslate.cs /tmp/plugin/RuntimeTranslation.cs /tmp/plugin/OtogiCgUnlock.cs \
    && rm -rf /tmp/plugin

COPY bootstrap/OtogiApplication.smali /opt/OtogiApplication.smali
COPY patch-sign.sh /usr/local/bin/patch-sign
RUN chmod 0755 /usr/local/bin/patch-sign

ENV LANG=C.UTF-8

ENTRYPOINT ["/usr/local/bin/patch-sign"]
