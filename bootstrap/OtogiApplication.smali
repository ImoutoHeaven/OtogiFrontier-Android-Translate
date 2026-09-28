.class public final Lorg/otogi/patcher/OtogiApplication;
.super Landroid/app/Application;

.method public constructor <init>()V
    .locals 0
    invoke-direct {p0}, Landroid/app/Application;-><init>()V
    return-void
.end method

.method public onCreate()V
    .locals 3
    invoke-super {p0}, Landroid/app/Application;->onCreate()V

    const/4 v0, 0x0
    invoke-virtual {p0, v0}, Landroid/content/Context;->getExternalFilesDir(Ljava/lang/String;)Ljava/io/File;
    move-result-object v0
    if-eqz v0, :failed

    new-instance v1, Ljava/io/File;
    const-string v2, "il2cpp"
    invoke-direct {v1, v0, v2}, Ljava/io/File;-><init>(Ljava/io/File;Ljava/lang/String;)V
    invoke-virtual {v1}, Ljava/io/File;->mkdirs()Z
    move-result v0
    if-nez v0, :ready
    invoke-virtual {v1}, Ljava/io/File;->isDirectory()Z
    move-result v0
    if-nez v0, :ready

    :failed
    new-instance v0, Ljava/lang/IllegalStateException;
    const-string v1, "Otogi bootstrap: external IL2CPP directory is unavailable"
    invoke-direct {v0, v1}, Ljava/lang/IllegalStateException;-><init>(Ljava/lang/String;)V
    throw v0

    :ready
    const-string v0, "OtogiBootstrap"
    const-string v1, "IL2CPP directory ready"
    invoke-static {v0, v1}, Landroid/util/Log;->i(Ljava/lang/String;Ljava/lang/String;)I
    return-void
.end method
