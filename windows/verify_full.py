import socket
import time
import json
import os
import base64

# 1x1 红色 PNG（模拟通知图标）
PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="
)
ICON = base64.b64encode(PNG).decode()

s = socket.create_connection(('127.0.0.1', 17001), timeout=5)
f = s.makefile('rwb', buffering=0)

def send(obj):
    line = json.dumps(obj, ensure_ascii=False) + '\n'
    f.write(line.encode('utf-8'))
    f.flush()

def recv(timeout=5):
    s.settimeout(timeout)
    return f.readline().decode('utf-8').strip()

# 1) 配对（预置信任）
send({'type': 'pair.request', 'id': 1, 'body': {
    'deviceId': 'smoke-test-device', 'deviceName': 'SmokeTestPhone',
    'platform': 'android', 'version': 1}})
print('PAIR ->', recv())

# 2) 带完整字段的通知（图标 + 会话 + 动作 + isClearable + silent）
send({'type': 'notification', 'id': 2, 'body': {
    'id': 'n-full', 'appName': '微信', 'packageName': 'com.tencent.mm',
    'title': '群名', 'text': '张三: 今晚吃饭吗？',
    'ticker': '群名: 张三: 今晚吃饭吗？', 'time': 1700000000000,
    'icon': ICON, 'isCancel': False, 'silent': False, 'isClearable': True,
    'conversation': [{'sender': '张三', 'content': '今晚吃饭吗？'},
                     {'sender': '李四', 'content': '算我一个'}],
    'groupName': '群名',
    'actions': ['回复', '删除'],
    'requestReplyId': 'n-full'}})
time.sleep(0.6)

# 3) isCancel=true 的通知（不应落盘）
send({'type': 'notification', 'id': 3, 'body': {
    'id': 'n-full', 'appName': '微信', 'packageName': 'com.tencent.mm',
    'time': 1700000000000, 'isCancel': True, 'silent': False}})
time.sleep(0.3)

# 4) 结束
time.sleep(0.8)
s.close()

path = os.path.join(os.environ['LOCALAPPDATA'], 'MessageBridge', 'messages.json')
with open(path, encoding='utf-8') as fp:
    data = json.load(fp)
print('=== messages count:', len(data))
for m in data:
    print('kind=%s source=%s title=%s content=%s icon_len=%s' % (
        m['Kind'], m['Source'], m['Title'], m['Content'],
        len(m.get('IconBase64', ''))))
