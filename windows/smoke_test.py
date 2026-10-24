import socket
import time
import json
import os

s = socket.create_connection(('127.0.0.1', 17001), timeout=5)
f = s.makefile('rwb', buffering=0)

def send(obj):
    line = json.dumps(obj, ensure_ascii=False) + '\n'
    f.write(line.encode('utf-8'))
    f.flush()

def recv(timeout=5):
    s.settimeout(timeout)
    return f.readline().decode('utf-8').strip()

# 1) ping -> pong
send({'type': 'ping', 'id': 1, 'body': {'time': 123}})
print('PING ->', recv())

# 2) pair.request（trust.json 已预置 smoke-test-device -> 应直接放行 pair.ok）
send({'type': 'pair.request', 'id': 2, 'body': {
    'deviceId': 'smoke-test-device', 'deviceName': 'SmokeTestPhone',
    'platform': 'android', 'version': 1}})
print('PAIR ->', recv())

# 3) notification
send({'type': 'notification', 'id': 3, 'body': {
    'id': 'n1', 'appName': '微信', 'packageName': 'com.tencent.mm',
    'title': '测试通知', 'text': '这是一条冒烟测试通知',
    'time': 1700000000000, 'isCancel': False, 'silent': False}})
time.sleep(1.0)
s.close()

path = os.path.join(os.environ['LOCALAPPDATA'], 'MessageBridge', 'messages.json')
print('=== messages.json ===')
with open(path, encoding='utf-8') as fp:
    print(fp.read())
