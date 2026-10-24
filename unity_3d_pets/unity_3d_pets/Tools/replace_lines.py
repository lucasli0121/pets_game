"""按行号区间替换文件内容。用于绕开 Edit 工具对长中文注释的匹配敏感问题。

用法：给 START/END（1-based，含两端）与新内容文件路径。
"""
import sys

path, newfile = sys.argv[1], sys.argv[2]
start, end = int(sys.argv[3]), int(sys.argv[4])

lines = open(path, 'rb').read().split(b'\n')
new = open(newfile, 'rb').read().rstrip(b'\n').split(b'\n')

print(f'替换前 {len(lines)} 行，将第 {start}-{end} 行换成 {len(new)} 行')
out = lines[:start - 1] + new + lines[end:]
open(path, 'wb').write(b'\n'.join(out))
print(f'替换后 {len(out)} 行')
