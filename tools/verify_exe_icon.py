"""直接从 exe 的 PE 资源段里把 RT_GROUP_ICON / RT_ICON 挖出来，
证明图标确实进了 exe（而不是只有窗口运行时才设置）。"""
import struct, sys, os

exe = r"C:\Users\yuwei\Documents\pcl2-like\src\PclLike.Gui\bin\Debug\net8.0-windows\PclLike.exe"
data = open(exe, "rb").read()

pe = struct.unpack_from("<I", data, 0x3C)[0]
assert data[pe:pe+4] == b"PE\x00\x00", "不是 PE"

nsect = struct.unpack_from("<H", data, pe+6)[0]
optsize = struct.unpack_from("<H", data, pe+20)[0]
opt = pe + 24
dd = opt + 112                      # PE32+ data directory
res_rva, res_size = struct.unpack_from("<II", data, dd + 2*8)
print(f"资源目录 RVA=0x{res_rva:X} size={res_size}")

sects = []
sec_off = opt + optsize
for i in range(nsect):
    o = sec_off + i*40
    name = data[o:o+8].rstrip(b"\x00").decode(errors="replace")
    vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", data, o+8)
    sects.append((name, vaddr, vsize, rawptr, rawsize))
    print(f"  {name:<8} vaddr=0x{vaddr:X} vsize=0x{vsize:X} raw=0x{rawptr:X}")

def rva2off(rva):
    for name, vaddr, vsize, rawptr, rawsize in sects:
        if vaddr <= rva < vaddr + max(vsize, rawsize):
            return rawptr + (rva - vaddr)
    return None

base = rva2off(res_rva)
print(f"资源段文件偏移 = 0x{base:X}")

# 遍历资源目录，找 type 3 (RT_ICON) 和 type 14 (RT_GROUP_ICON)
def walk(off, level, path, found):
    n_named, n_id = struct.unpack_from("<HH", data, off+12)
    total = n_named + n_id
    for i in range(total):
        e = off + 16 + i*8
        name_or_id, sub = struct.unpack_from("<II", data, e)
        if level == 0:
            found.setdefault(name_or_id & 0x7FFFFFFF, 0)
        if sub & 0x80000000:
            walk(base + (sub & 0x7FFFFFFF), level+1, path+[name_or_id], found)
        else:
            found[name_or_id & 0x7FFFFFFF] = found.get(name_or_id & 0x7FFFFFFF, 0) + 1

found = {}
walk(base, 0, [], found)
TYPE = {1:"RT_CURSOR",2:"RT_BITMAP",3:"RT_ICON",14:"RT_GROUP_ICON",16:"RT_VERSION",24:"RT_MANIFEST"}
print("\n资源类型清单:")
for t, cnt in sorted(found.items()):
    print(f"  type {t:>3}  {TYPE.get(t,'(其它)'):<14} 出现 {cnt} 次")

has_icon = 3 in found
has_group = 14 in found
print(f"\nRT_ICON 存在      : {has_icon}")
print(f"RT_GROUP_ICON 存在: {has_group}")
print("结论: " + ("exe 内已嵌入图标资源 ✓" if (has_icon and has_group) else "未找到图标资源 ✗"))