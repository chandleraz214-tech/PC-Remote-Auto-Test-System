#!/usr/bin/env python3
import asyncio
import json
import threading
from flask import Flask, render_template_string, request, jsonify
import requests
import websockets

app = Flask(__name__)

# 舵机 API（根据实际修改 IP 和参数）
SERVO_API = "http://192.168.5.122/doNow?pushDepth=20&lastTime=1&continueNum=1&continueTime=1"

# 存储所有连接的客户端
connected_clients = set()

# ========== HTML 页面 ==========
HTML = """
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>PC 远程控制中心</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: Arial, sans-serif; background: #f0f2f5; padding: 20px; }
        .container { max-width: 700px; margin: 0 auto; background: #fff; border-radius: 10px; padding: 30px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        h1 { color: #333; margin-bottom: 10px; }
        .status-bar { padding: 10px; border-radius: 5px; margin-bottom: 15px; font-weight: bold; }
        .status-online { background: #c8e6c9; color: #2e7d32; }
        .status-offline { background: #ffcdd2; color: #c62828; }
        .btn-group { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 15px; }
        .btn { padding: 10px 20px; border: none; border-radius: 5px; font-size: 14px; cursor: pointer; transition: 0.3s; }
        .btn:hover { opacity: 0.8; transform: scale(0.97); }
        .btn-memory { background: #4fc3f7; }
        .btn-shutdown { background: #ff8a65; color: #fff; }
        .btn-restart { background: #ffb74d; color: #fff; }
        .btn-sleep { background: #fff176; }
        .btn-test { background: #81c784; color: #fff; }
        .btn-custom { background: #aed581; color: #fff; }
        .btn-result { background: #e57373; color: #fff; }
        .btn-wake { background: #ce93d8; color: #fff; }
        .log { margin-top: 20px; background: #263238; color: #a6e22e; padding: 15px; border-radius: 5px; font-family: monospace; min-height: 100px; max-height: 300px; overflow-y: auto; white-space: pre-wrap; font-size: 13px; }
        .log-error { color: #ff6b6b; }
        .log-success { color: #69db7c; }
        .log-info { color: #74c0fc; }
    </style>
</head>
<body>
    <div class="container">
        <h1>🔧 PC 远程控制中心</h1>
        <div id="statusBar" class="status-bar status-offline">⛔ 设备离线</div>
        <div class="btn-group">
            <button class="btn btn-memory" onclick="sendCmd('memory')">📊 内存信息</button>
            <button class="btn btn-shutdown" onclick="sendCmd('shutdown')">⏻ 关机</button>
            <button class="btn btn-restart" onclick="sendCmd('restart')">🔄 重启</button>
            <button class="btn btn-sleep" onclick="sendCmd('sleep')">💤 睡眠</button>
            <button class="btn btn-test" onclick="sendCmd('test')">🧪 标准压测 (3h)</button>
            <button class="btn btn-custom" onclick="sendCmd('customtest')">🧪 自定义压测 (15m)</button>
            <button class="btn btn-result" onclick="sendCmd('getresult')">📄 获取结果</button>
            <button class="btn btn-wake" onclick="physicalWake()">🔌 物理唤醒</button>
        </div>
        <div class="log" id="log">等待操作...</div>
    </div>
    <script>
        var ws = null;
        var isConnected = false;

        function connect() {
            ws = new WebSocket('ws://192.168.5.108:8765');
            ws.onopen = function() {
                isConnected = true;
                document.getElementById('statusBar').className = 'status-bar status-online';
                document.getElementById('statusBar').textContent = '✅ 设备在线';
                addLog('✅ 已连接到服务端', 'success');
            };
            ws.onerror = function(e) {
                addLog('❌ 连接错误', 'error');
            };
            ws.onclose = function() {
                isConnected = false;
                document.getElementById('statusBar').className = 'status-bar status-offline';
                document.getElementById('statusBar').textContent = '⛔ 设备离线';
                addLog('连接已断开，5秒后重连...', 'info');
                setTimeout(connect, 5000);
            };
            ws.onmessage = function(e) {
                try {
                    var data = JSON.parse(e.data);
                    if (data.type === 'result') {
                        addLog('✅ ' + data.result, 'success');
                    }
                } catch(err) {
                    addLog('收到: ' + e.data, 'info');
                }
            };
        }

        function sendCmd(cmd) {
            if (!isConnected) {
                addLog('❌ 设备离线，无法发送命令', 'error');
                return;
            }
            ws.send(JSON.stringify({ command: cmd }));
            addLog('▶ 发送: ' + cmd, 'info');
        }

        function physicalWake() {
            fetch('/cmd/physicalwake')
                .then(res => res.json())
                .then(data => {
                    addLog('🔌 唤醒: ' + JSON.stringify(data), 'success');
                })
                .catch(err => {
                    addLog('❌ 唤醒失败: ' + err, 'error');
                });
        }

        function addLog(msg, type) {
            var log = document.getElementById('log');
            var time = new Date().toLocaleTimeString();
            var cls = '';
            if (type === 'error') cls = 'log-error';
            else if (type === 'success') cls = 'log-success';
            else if (type === 'info') cls = 'log-info';
            log.innerHTML += '<div class="' + cls + '">[' + time + '] ' + msg + '</div>';
            log.scrollTop = log.scrollHeight;
        }

        connect();
        addLog('正在连接...', 'info');
    </script>
</body>
</html>
"""

@app.route('/')
def index():
    return render_template_string(HTML)

@app.route('/cmd/physicalwake')
def physical_wake():
    try:
        resp = requests.get(SERVO_API, timeout=5)
        return jsonify({'status': 'ok', 'message': '唤醒指令已发送'})
    except Exception as e:
        return jsonify({'status': 'error', 'message': str(e)})

# ========== WebSocket 转发逻辑 ==========
async def handle_ws(websocket):
    print(f"[WebSocket] 新连接: {websocket.remote_address}")
    connected_clients.add(websocket)
    
    try:
        async for message in websocket:
            try:
                data = json.loads(message)
                
                # 注册消息（来自华为客户端）
                if data.get('type') == 'register':
                    client_name = data.get('client', 'unknown')
                    print(f"[注册] 华为客户端: {client_name}")
                    await websocket.send(json.dumps({
                        'type': 'result',
                        'result': f'注册成功: {client_name}'
                    }))
                    continue
                
                # 命令结果（来自华为客户端）
                if data.get('type') == 'result':
                    result_text = data.get('result', '')
                    print(f"[结果] {result_text}")
                    for client in list(connected_clients):
                        if client != websocket:
                            try:
                                await client.send(json.dumps({
                                    'type': 'result',
                                    'result': result_text
                                }))
                            except:
                                pass
                    continue
                
                # 命令（来自浏览器）
                cmd = data.get('command')
                if cmd:
                    print(f"[命令] 收到: {cmd}")
                    forwarded = False
                    for client in list(connected_clients):
                        if client != websocket:
                            try:
                                await client.send(json.dumps({'command': cmd}))
                                forwarded = True
                                print(f"[转发] 命令已发送到华为客户端")
                                break
                            except:
                                pass
                    
                    if not forwarded:
                        await websocket.send(json.dumps({
                            'type': 'result',
                            'result': '❌ 没有华为客户端连接'
                        }))
                        
            except json.JSONDecodeError as e:
                print(f"[错误] JSON解析失败: {e}")
                await websocket.send(json.dumps({
                    'type': 'result',
                    'result': '无效的JSON格式'
                }))
                
    except websockets.exceptions.ConnectionClosed:
        print(f"[WebSocket] 连接断开: {websocket.remote_address}")
    except Exception as e:
        print(f"[WebSocket] 错误: {e}")
    finally:
        connected_clients.discard(websocket)

async def ws_server():
    async with websockets.serve(handle_ws, "0.0.0.0", 8765):
        print("========================================")
        print("   WebSocket 服务已启动")
        print("   端口: 8765")
        print("========================================")
        await asyncio.Future()

# ========== 启动 ==========
if __name__ == '__main__':
    print("========================================")
    print("   PC 远程控制中心 (Mac 服务端)")
    print("========================================")
    print(f"   Mac IP: 192.168.5.108")
    print(f"   HTTP: http://192.168.5.108:5001")
    print(f"   WebSocket: ws://192.168.5.108:8765")
    print("========================================")
    
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    threading.Thread(target=lambda: loop.run_until_complete(ws_server()), daemon=True).start()
    
    app.run(host='0.0.0.0', port=5001, debug=False)