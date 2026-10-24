"""GBK 乱码回滚：把被 PowerShell 5.1 双重编码写坏的源文件还原成 UTF-8。

原理：
  PS 5.1 的 Get-Content 默认按 ANSI(936/GBK) 解码字节流，
  Set-Content -Encoding utf8 再把得到的字符串按 UTF-8 写回。
  所以磁盘上的字节 = utf8( gbk_decode( 原来的 utf8 字节 ) )。
  反推：读出字符串 → 按 GBK 编码回字节 → 按 UTF-8 解码 = 原文。

有损之处：GBK 无法映射的尾字节被替换成 '?'，那些位置不可逆。
"""
import sys

def repair(path, dry=True):
    raw = open(path, 'rb').read()
    bom = raw.startswith(b'\xef\xbb\xbf')
    if bom:
        raw = raw[3:]

    moji = raw.decode('utf-8', errors='replace')

    try:
        back = moji.encode('gbk')
    except UnicodeEncodeError as e:
        print('  [x] GBK 编码失败：%s' % e)
        return None

    text = back.decode('utf-8', errors='replace')

    bad = text.count('\ufffd')
    q = moji.count('?')
    print('  BOM=%s  乱码串长度=%d  "?"个数=%d  还原后替换符=%d'
          % (bom, len(moji), q, bad))

    if not dry:
        open(path, 'wb').write(text.encode('utf-8'))
    return text


for p in sys.argv[1:]:
    print('== %s' % p)
    t = repair(p, dry=True)
    if t is None:
        continue
    lines = t.split('\n')
    # 抽几行看清楚
    for idx in (0, 1, 5, 225, 228):
        if idx < len(lines):
            print('   L%-4d %s' % (idx + 1, lines[idx].rstrip()[:110]))