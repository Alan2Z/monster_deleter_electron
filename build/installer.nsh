; electron-builder NSIS 自定义脚本
;
; 安装目录:向导模式下模板自带 instFilesPre 会在用户选的目录后追加
; \MonsterDeleter 子目录(避免选到盘符根目录);但静默安装(/S)没有页面,
; 该回调不触发,这里在 customInit 里补同样的逻辑(只处理盘符根目录)。
; 本脚本还负责:卸载时清理右键菜单注册表残留。

!macro customInit
  ${If} ${Silent}
    ; 静默安装:用户 /D 指定的盘符根目录(D: 或 D:\) → 追加应用子目录
    StrLen $R0 $INSTDIR
    ${If} $R0 = 2                 ; "D:"
      StrCpy $R1 $INSTDIR 1 1
      ${If} $R1 == ":"
        StrCpy $INSTDIR "$INSTDIR\${APP_FILENAME}"
      ${EndIf}
    ${ElseIf} $R0 = 3             ; "D:\"
      StrCpy $R1 $INSTDIR 1 1
      StrCpy $R2 $INSTDIR 1 2
      ${If} $R1 == ":"
      ${AndIf} $R2 == "\"
        StrCpy $INSTDIR "$INSTDIR${APP_FILENAME}"
      ${EndIf}
    ${EndIf}
  ${EndIf}
!macroend

!macro customUnInstall
  ; 只清右键菜单注册表(文件 * + 文件夹 Directory);InstallLocation("记住的
  ; 安装位置")是用户的选择,保留它,重装时自动沿用上次目录
  DeleteRegKey HKCU "Software\Classes\*\shell\SummonMonster"
  DeleteRegKey HKCU "Software\Classes\Directory\shell\SummonMonster"
!macroend
