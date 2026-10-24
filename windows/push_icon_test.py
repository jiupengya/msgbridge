import socket
import time
import json
import base64
import struct
import zlib

def make_png_icon():
    """生成 64x64 纯色 PNG（红色圆 + 透明底），模拟通知应用图标。"""
    W = H = 64
    rows = []
    for y in range(H):
        row = b'\x00'
        for x in range(W):
            # 画红色圆
            dx, dy = x - 32, y - 32
            inside = dx*dx + dy*dy <= 24*24
            if inside:
                row += bytes([255, 64, 64, 255])   # RGBA 红
            else:
                row += bytes([0, 0, 0, 0])
        rows.append(row)

    def chunk(tag, data):
        c = tag + data
        return c + struct.pack('>I', zlib.crc32(c) & 0xffffffff)

    sig = b'\x89PNG\r\n\x1a\n'
    ihdr = struct.pack('>IIBBBBB', W, H, 8, 6, 0, 0, 0)
    idat = zlib.compress(b''.join(rows))
    png = (sig + chunk(b'IHDR', ihdr) + chunk(b'IDAT', idat) + chunk(b'IEND', b''))
    return base64.b64encode(png).decode()

icon = make_png_icon()
print('icon b64 len:', len(icon))

s = socket.create_connection(('127.0.0.1', 17001), timeout=5)
f = s.makefile('rwb', buffering=0)

def send(obj):
    line = json.dumps(obj, ensure_ascii=False) + '\n'
    f.write(line.encode('utf-8'))
    f.flush()

def recv(timeout=5):
    s.settimeout(timeout)
    return f.readline().decode('utf-8').strip()

# 用已信任设备 ID 配对（trust.json 已有该设备 -> 直接放行）
send({'type': 'pair.request', 'id': 1, 'body': {
    'deviceId': '7a0d2c1e7b861e9c', 'deviceName': 'Xiaomi 2407FRK8EC',
    'platform': 'android', 'version': 1}})
print('PAIR ->', recv())

# 推送带图标的通知
send({'type': 'notification', 'id': 2, 'body': {
    'id': 'toast-demo', 'appName': '测试应用', 'packageName': 'com.demo',
    'title': '图标弹窗测试', 'text': '这条通知应该显示红色圆图标',
    'time': int(time.time() * 1000), 'icon': icon,
    'isCancel': False, 'silent': False, 'isClearable': True}})
time.sleep(1.0)
s.close()
print('notification sent')
