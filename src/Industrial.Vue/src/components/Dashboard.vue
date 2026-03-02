<template>
  <div class="screen-container">
    <header class="screen-header">
      <div class="header-left">
        <button class="tech-btn" @click="connectSignalR" :disabled="isConnected">
          <span class="btn-text">打开链接</span>
        </button>
        <button class="tech-btn danger" @click="disconnectSignalR" :disabled="!isConnected">
          <span class="btn-text">断开链接</span>
        </button>
      </div>
      
      <div class="header-center">
        <div class="title-bg">
          <h1>工业大数据可视化平台</h1>
        </div>
      </div>
      
      <div class="header-right">
        <div class="status-indicator">
          <span class="status-label">SYS_STATE:</span>
          <span :class="['status-value', isConnected ? 'online' : 'offline']">
            {{ isConnected ? 'ONLINE' : 'OFFLINE' }}
          </span>
        </div>
        <div class="time-display">{{ currentTime }}</div>
      </div>
    </header>

    <div class="screen-body">
      <!-- Left Column -->
      <div class="col col-left">
        <div class="tech-panel h-50" style="flex: 1;">
          <div class="panel-header">机床作业状态流</div>
          <div class="panel-content scrollable">
            <div class="state-list" style="position: relative;">
              <transition-group name="fade">
                <div v-for="state in machineStates" :key="state.id" class="tech-list-item state-item">
                  <span class="time">[{{ state.time.substring(11) }}]</span>
                  <span class="code">CODE:{{ state.code }}</span>
                  <span class="desc">{{ state.description }}</span>
                </div>
              </transition-group>
            </div>
            <div v-if="!machineStates.length" class="empty-text">等待机床反馈...</div>
          </div>
        </div>

        <div class="tech-panel h-50" style="flex: 1;">
          <div class="panel-header">温度预警监测曲线</div>
          <div class="panel-content" ref="tempChartRef" style="height: 100%;"></div>
        </div>
      </div>

      <!-- Center Column -->
      <div class="col col-center">
        <div class="number-cards-container">
          <div class="num-card">
            <div class="num-title">今日累计任务数</div>
            <div class="num-value neon-text-blue">120.0</div>
          </div>
          <div class="num-card">
            <div class="num-title">本月累计任务数</div>
            <div class="num-value neon-text-green">18.0</div>
          </div>
          <div class="num-card">
            <div class="num-title">异常阻断次数</div>
            <div class="num-value neon-text-red">2.0</div>
          </div>
        </div>

        <div class="tech-panel large-chart h-auto">
          <div class="panel-header">综合数据统计图</div>
          <div class="panel-content" ref="mainChartRef" style="height: 100%; min-height: 400px;"></div>
        </div>
      </div>

      <!-- Right Column -->
      <div class="col col-right">
        <!-- 1. Controls (Top Panel) -->
        <div class="tech-panel" style="flex: 0 0 auto;">
          <div class="panel-header">控制与调度台</div>
          <div class="panel-content control-content" style="padding-bottom: 5px;">
            <div class="input-group">
              <input type="text" v-model="logMessage" class="tech-input" placeholder="输入调度指令..." :disabled="!isConnected"/>
              <button class="tech-btn small" @click="sendLog" :disabled="!isConnected || !logMessage">下发</button>
            </div>
            <div class="action-row">
              <button class="tech-btn glow-orange w-full current-temp-btn" @click="triggerCurrentTemperature" :disabled="!isConnected">
                <span class="label">⟳ 手动拉取实时机床温度</span>
                <span class="value neon-text-orange">{{ currentTemp > 0 ? currentTemp.toFixed(1) + ' °C' : '-- °C' }}</span>
              </button>
            </div>
          </div>
        </div>

        <!-- 2. System Logs (Middle Auto Panel) -->
        <div class="tech-panel h-auto">
          <div class="panel-header">系统事件追踪日志</div>
          <div class="panel-content scrollable">
            <div class="sys-log-list" style="position: relative;">
              <transition-group name="fade">
                <div v-for="log in systemLogs" :key="log.id" class="tech-list-item sys-log-item">
                  <span class="time">[{{ log.time.substring(11) }}]</span>
                  <span :class="['msg', log.type]">{{ log.message }}</span>
                </div>
              </transition-group>
            </div>
            <div v-if="!systemLogs.length" class="empty-text">等待系统互联...</div>
          </div>
        </div>

        <!-- 3. Radar Chart (Bottom Panel) -->
        <div class="tech-panel" style="flex: 0 0 35%; height: 35%;">
          <div class="panel-header">综合质量雷达分析</div>
          <div class="panel-content" ref="radarChartRef" style="height: 100%;"></div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, nextTick } from 'vue';
import * as signalR from '@microsoft/signalr';
import * as echarts from 'echarts';

const isConnected = ref(false);
const temperatureAlerts = ref<{time: string, temp: number}[]>([]);
let stateId = 0;
const machineStates = ref<{id: number, time: string, code: number, description: string}[]>([]);
const currentTemp = ref(0.0);
const logMessage = ref('');
const currentTime = ref('');

// Add systemLogs and logging method
let logId = 0;
const systemLogs = ref<{id: number, time: string, message: string, type: 'info'|'success'|'warning'|'error'}[]>([]);

const addSysLog = (msg: string, type: 'info'|'success'|'warning'|'error' = 'info') => {
  systemLogs.value.unshift({ id: logId++, time: currentTime.value || new Date().toISOString().replace('T', ' '), message: msg, type });
  if (systemLogs.value.length > 50) systemLogs.value.pop();
};

const tempChartRef = ref<HTMLElement | null>(null);
const mainChartRef = ref<HTMLElement | null>(null);
const radarChartRef = ref<HTMLElement | null>(null);

let connection: signalR.HubConnection | null = null;
let tempChart: echarts.ECharts | null = null;
let mainChart: echarts.ECharts | null = null;
let radarChart: echarts.ECharts | null = null;
let timeInterval: ReturnType<typeof setInterval>;
let resizeObserver: ResizeObserver | null = null;

const updateTime = () => {
  const now = new Date();
  const year = now.getFullYear();
  const month = (now.getMonth() + 1).toString().padStart(2, '0');
  const date = now.getDate().toString().padStart(2, '0');
  const hours = now.getHours().toString().padStart(2, '0');
  const minutes = now.getMinutes().toString().padStart(2, '0');
  const seconds = now.getSeconds().toString().padStart(2, '0');
  currentTime.value = `${year}-${month}-${date} ${hours}:${minutes}:${seconds}`;
};

const connectSignalR = async () => {
  if (connection && isConnected.value) return;

  addSysLog('正在尝试建立 SignalR 长链接...', 'info');

  try {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('https://localhost:7212/hubs/deviceStatus')
      .withAutomaticReconnect()
      .build();

    connection.on('ReceiveTemperatureWarning', (deviceId: string, temp: number) => {
      temperatureAlerts.value.push({ time: currentTime.value.substring(11), temp });
      if (temperatureAlerts.value.length > 20) temperatureAlerts.value.shift();
      updateTempChart();
    });

    connection.on('UpdateMachineState', (stateCode: number, description: string) => {
      machineStates.value.unshift({ id: stateId++, time: currentTime.value, code: stateCode, description });
      if (machineStates.value.length > 30) machineStates.value.pop();
    });

    connection.on('ReceiveCurrentTemperature', (temp: number) => {
      currentTemp.value = temp;
      addSysLog(`强制下拉最新核心参数: ${temp.toFixed(1)}°C`, 'success');
    });

    connection.onreconnecting(() => { 
        isConnected.value = false; 
        addSysLog('与服务器连接骤然中断，正在自动重连...', 'warning');
    });
    connection.onreconnected(() => { 
        isConnected.value = true; 
        addSysLog('自动重新连接成功', 'success');
    });

    await connection.start();
    isConnected.value = true;
    addSysLog('SignalR 核心通道已成功打通', 'success');
  } catch (err) {
    console.error('SignalR 连通失败: ', err);
    isConnected.value = false;
    addSysLog(`SignalR 连通失败: ${err}`, 'error');
  }
};

const disconnectSignalR = async () => {
  if (connection) {
    addSysLog('正在主动断开大屏网关接口...', 'warning');
    await connection.stop();
    isConnected.value = false;
    connection = null;
    addSysLog('网络连接已安全卸载', 'error');
  }
};

const sendLog = async () => {
  if (connection && isConnected.value && logMessage.value) {
    try {
      addSysLog(`提交通讯流报文: ${logMessage.value}`, 'info');
      await connection.invoke('SendClientLogToServer', logMessage.value);
      addSysLog('中枢指令已成功安全抵达集群', 'success');
      logMessage.value = '';
    } catch (err) {
      console.error('Error sending log:', err);
      addSysLog(`报文投递遭受阻塞: ${err}`, 'error');
    }
  }
};

const triggerCurrentTemperature = async () => {
  try {
    addSysLog('发起 WebAPI (HTTP) 拉取目标参数指令', 'info');
    await fetch('https://localhost:7212/api/temperature/push_current', { method: 'POST' });
    addSysLog('HTTP轮询触发成功，正等待服务器强制下发...', 'info');
  } catch (err) {
    console.error('Error triggering current temperature:', err);
    addSysLog('越权访问目标接口握手失败', 'error');
  }
};

const updateTempChart = () => {
  if (tempChart) {
    tempChart.setOption({
      xAxis: { data: temperatureAlerts.value.map(a => a.time) },
      series: [{ data: temperatureAlerts.value.map(a => a.temp) }]
    });
  }
};

const initCharts = () => {
  if (tempChartRef.value) {
    tempChart = echarts.init(tempChartRef.value);
    tempChart.setOption({
      tooltip: { trigger: 'axis' },
      grid: { left: '3%', right: '4%', bottom: '5%', top: '15%', containLabel: true },
      xAxis: { type: 'category', data: [], axisLine: { lineStyle: { color: '#00f8ff' } }, axisLabel: { color: '#8eb5ff' } },
      yAxis: { type: 'value', min: 70, axisLine: { lineStyle: { color: '#00f8ff' } }, splitLine: { show: false } },
      series: [{ 
        name: '预警温度', 
        type: 'line', 
        smooth: true, 
        itemStyle: { color: '#ff3333' }, 
        lineStyle: { color: '#ff3333', width: 2 },
        areaStyle: { color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [{offset: 0, color: 'rgba(255,51,51,0.5)'}, {offset: 1, color: 'rgba(255,51,51,0)'}]) }, 
        data: [] 
      }]
    });
  }

  if (mainChartRef.value) {
    mainChart = echarts.init(mainChartRef.value);
    mainChart.setOption({
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      legend: { textStyle: { color: '#a0cfff' }, data: ['运行数量', '计划数量', '异常率'] },
      grid: { left: '3%', right: '4%', bottom: '5%', containLabel: true },
      xAxis: {
        type: 'category',
        data: ['万州', '北碚', '渝北', '长寿', '璧山', '江津', '永川', '南川', '黔江', '垫江'],
        axisLine: { lineStyle: { color: '#00f8ff' } },
        axisLabel: { color: '#8eb5ff' }
      },
      yAxis: [
        { type: 'value', axisLine: { lineStyle: { color: '#00f8ff' } }, splitLine: { lineStyle: { color: 'rgba(0, 248, 255, 0.1)' } } },
        { type: 'value', max: 1, splitLine: { show: false }, axisLine: { lineStyle: { color: '#00f8ff' } } }
      ],
      series: [
        { name: '运行数量', type: 'bar', barWidth: 15, itemStyle: { color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [{offset: 0, color: '#83bff6'}, {offset: 1, color: '#188df0'}]) }, data: [210, 310, 420, 520, 360, 410, 720, 390, 510, 490] },
        { name: '计划数量', type: 'bar', barWidth: 15, itemStyle: { color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [{offset: 0, color: '#e883f6'}, {offset: 1, color: '#7a18f0'}]) }, data: [250, 350, 450, 550, 400, 450, 800, 450, 550, 550] },
        { name: '异常率', type: 'line', yAxisIndex: 1, smooth: true, lineStyle: { color: '#00f8ff', width: 2 }, itemStyle: { color: '#00f8ff' }, areaStyle: { color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [{offset: 0, color: 'rgba(0,248,255,0.4)'}, {offset: 1, color: 'rgba(0,248,255,0)'}]) }, data: [0.1, 0.15, 0.08, 0.12, 0.05, 0.2, 0.1, 0.04, 0.18, 0.1] }
      ]
    });
  }

  if (radarChartRef.value) {
    radarChart = echarts.init(radarChartRef.value);
    radarChart.setOption({
      tooltip: {},
      radar: {
        indicator: [ { name: '加工精度', max: 100 }, { name: '作业效率', max: 100 }, { name: '能耗情况', max: 100 }, { name: '维护状态', max: 100 }, { name: '故障率', max: 100 } ],
        splitLine: { lineStyle: { color: 'rgba(0, 248, 255, 0.2)' } },
        splitArea: { show: false },
        axisLine: { lineStyle: { color: 'rgba(0, 248, 255, 0.2)' } }
      },
      series: [{
        name: '能力分析',
        type: 'radar',
        data: [
          { value: [80, 90, 70, 85, 60], name: 'A车间', itemStyle: { color: '#00f8ff' }, areaStyle: { color: 'rgba(0, 248, 255, 0.3)' } },
          { value: [95, 80, 85, 90, 40], name: 'B车间', itemStyle: { color: '#e883f6' }, areaStyle: { color: 'rgba(232, 131, 246, 0.3)' } }
        ]
      }]
    });
  }
};

onMounted(() => {
  updateTime();
  timeInterval = setInterval(updateTime, 1000);
  
  // 延迟渲染图表以等待DOM
  nextTick(() => {
    initCharts();
    
    // 使用 ResizeObserver 完美解决自适应问题
    resizeObserver = new ResizeObserver(() => {
      mainChart?.resize();
      radarChart?.resize();
      tempChart?.resize();
    });
    if (mainChartRef.value) resizeObserver.observe(mainChartRef.value);
    if (radarChartRef.value) resizeObserver.observe(radarChartRef.value);
    if (tempChartRef.value) resizeObserver.observe(tempChartRef.value);
  });
});

onUnmounted(() => {
  clearInterval(timeInterval);
  resizeObserver?.disconnect();
  if (connection) connection.stop();
  mainChart?.dispose();
  radarChart?.dispose();
  tempChart?.dispose();
});
</script>

<style scoped>
/* 全局科幻界面设计 */
.screen-container {
  width: 100vw;
  height: 100vh;
  margin: 0;
  padding: 0;
  background: #010B19 url('data:image/svg+xml;utf8,<svg xmlns="http://www.w3.org/2000/svg" width="con" height="100%"><rect width="100%" height="100%" fill="none" /></svg>') no-repeat center center/cover;
  background-image: radial-gradient(circle at 50% 50%, rgba(1, 19, 50, 1) 0%, rgba(1, 8, 18, 1) 100%);
  color: #fff;
  font-family: 'Inter', system-ui, sans-serif;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

/* 头部 Header */
.screen-header {
  height: 80px;
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  padding: 0 40px;
  background: url('data:image/svg+xml;utf8,<svg width="100%" height="10px" xmlns="http://www.w3.org/2000/svg"><line x1="0" y1="5" x2="100%" y2="5" stroke="rgba(0,248,255,0.3)" stroke-width="1"/></svg>') bottom repeat-x;
  position: relative;
}

.header-center {
  flex: 1;
  display: flex;
  justify-content: center;
  position: relative;
  top: 10px;
}

.title-bg {
  width: 600px;
  height: 60px;
  background: polygon(10% 0, 90% 0, 100% 100%, 0 100%); /* 伪梯形，此处采用边框特效模拟 */
  border-bottom: 2px solid #00f8ff;
  border-left: 1px solid rgba(0, 248, 255, 0.5);
  border-right: 1px solid rgba(0, 248, 255, 0.5);
  box-shadow: 0 5px 20px rgba(0, 248, 255, 0.3) inset;
  display: flex;
  align-items: center;
  justify-content: center;
  clip-path: polygon(5% 0, 95% 0, 100% 100%, 0 100%);
  background: rgba(0, 30, 80, 0.4);
}

.title-bg h1 {
  margin: 0;
  font-size: 32px;
  font-weight: bold;
  letter-spacing: 4px;
  text-shadow: 0 0 10px #00f8ff, 0 0 20px #00f8ff;
  color: #fff;
}

.header-left, .header-right {
  width: 300px;
  display: flex;
  align-items: center;
  height: 60px;
  padding-top: 10px;
}

.header-left {
  gap: 15px;
}

.header-right {
  justify-content: flex-end;
  gap: 20px;
}

.time-display {
  font-size: 18px;
  color: #00f8ff;
  font-weight: 500;
  letter-spacing: 1px;
}

.status-indicator {
  font-size: 16px;
  background: rgba(0,0,0,0.5);
  padding: 5px 15px;
  border: 1px solid #00f8ff;
  border-radius: 4px;
  box-shadow: 0 0 10px rgba(0, 248, 255, 0.2) inset;
}

.status-label {
  color: #76adc5;
  margin-right: 8px;
}

.status-value.online {
  color: #00ff00;
  text-shadow: 0 0 5px #00ff00;
  font-weight: bold;
}
.status-value.offline {
  color: #ff3333;
  text-shadow: 0 0 5px #ff3333;
  font-weight: bold;
}

/* 按钮科幻特效 */
.tech-btn {
  background: rgba(0, 60, 120, 0.4);
  border: 1px solid #00f8ff;
  color: #fff;
  padding: 8px 20px;
  font-size: 16px;
  cursor: pointer;
  position: relative;
  overflow: hidden;
  transition: all 0.3s;
  box-shadow: 0 0 10px rgba(0, 248, 255, 0.2) inset;
}

.tech-btn:hover:not(:disabled) {
  background: rgba(0, 248, 255, 0.2);
  box-shadow: 0 0 20px rgba(0, 248, 255, 0.6) inset;
}

.tech-btn:disabled {
  border-color: #444;
  color: #666;
  background: rgba(20, 20, 20, 0.4);
  cursor: not-allowed;
  box-shadow: none;
}

.tech-btn.danger {
  border-color: #ff3333;
  box-shadow: 0 0 10px rgba(255, 51, 51, 0.2) inset;
}
.tech-btn.danger:hover:not(:disabled) {
  background: rgba(255, 51, 51, 0.2);
  box-shadow: 0 0 20px rgba(255, 51, 51, 0.6) inset;
}

.tech-btn.glow-red {
  background: rgba(255, 0, 0, 0.2);
}

.tech-btn.glow-orange {
  background: rgba(255, 140, 0, 0.2);
  border-color: #ff8c00;
  box-shadow: 0 0 10px rgba(255, 140, 0, 0.2) inset;
}
.tech-btn.glow-orange:hover:not(:disabled) {
  background: rgba(255, 140, 0, 0.3);
  box-shadow: 0 0 20px rgba(255, 140, 0, 0.6) inset;
}
.current-temp-btn {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 12px 20px;
}
.neon-text-orange { color: #ff8c00; text-shadow: 0 0 10px #ff8c00; font-size: 20px; font-weight: bold; }

.tech-btn.small {
  padding: 6px 12px;
  font-size: 14px;
}

/* 主体布局 Grid */
.screen-body {
  flex: 1;
  display: flex;
  padding: 15px;
  gap: 15px;
  overflow: hidden;
}

.col {
  display: flex;
  flex-direction: column;
  gap: 15px;
}

.col-left { width: 400px; }
.col-right { width: 400px; }
.col-center { flex: 1; min-width: 0; }

.h-30 { height: 35%; }
.h-50 { height: 50%; }
.h-70 { height: 65%; }
.h-auto { flex: 1; }
.w-full { width: 100%; }

/* 面板通用样式 (科幻边框) */
.tech-panel {
  position: relative;
  background: rgba(2, 17, 40, 0.7);
  border: 1px solid rgba(0, 150, 255, 0.3);
  display: flex;
  flex-direction: column;
  box-shadow: 0 0 15px rgba(0, 100, 255, 0.1) inset;
}

.tech-panel::before {
  content: '';
  position: absolute;
  top: -1px; left: -1px;
  width: 20px; height: 20px;
  border-top: 2px solid #00f8ff;
  border-left: 2px solid #00f8ff;
  z-index: 1;
}
.tech-panel::after {
  content: '';
  position: absolute;
  bottom: -1px; right: -1px;
  width: 20px; height: 20px;
  border-bottom: 2px solid #00f8ff;
  border-right: 2px solid #00f8ff;
  z-index: 1;
}

.panel-header {
  height: 40px;
  line-height: 40px;
  padding-left: 15px;
  font-size: 16px;
  font-weight: bold;
  color: #fff;
  background: linear-gradient(90deg, rgba(0, 150, 255, 0.4) 0%, rgba(0, 150, 255, 0) 100%);
  border-left: 4px solid #00f8ff;
  margin-top: 10px;
}

.panel-content {
  flex: 1;
  padding: 15px;
  overflow: hidden;
  position: relative;
}

.scrollable {
  overflow-y: auto;
}
.scrollable::-webkit-scrollbar { width: 4px; }
.scrollable::-webkit-scrollbar-track { background: transparent; }
.scrollable::-webkit-scrollbar-thumb { background: rgba(0, 248, 255, 0.4); border-radius: 2px; }

/* 列表数据 */
.tech-list-item {
  display: flex;
  padding: 8px 10px;
  background: rgba(0, 100, 255, 0.1);
  margin-bottom: 8px;
  font-size: 14px;
  border: 1px solid rgba(0, 248, 255, 0.1);
}
.tech-list-item:hover {
  background: rgba(0, 248, 255, 0.15);
  border-color: rgba(0, 248, 255, 0.5);
}
.tech-list-item .time { color: #8eb5ff; min-width: 80px; }
.alert-item .device { color: #fff; flex: 1; }
.alert-item .value { color: #ff3333; font-weight: bold; text-shadow: 0 0 5px rgba(255,0,0,0.5); }
.state-item .code { color: #00f8ff; width: 80px; font-weight: bold; }
.state-item .desc { color: #e2e8f0; flex: 1; }

.empty-text {
  text-align: center;
  color: #4a6b8c;
  margin-top: 20px;
  font-style: italic;
}

/* System Logs */
.sys-log-item {
  border-left: 2px solid transparent;
}
.sys-log-item .msg {
  flex: 1;
  word-break: break-all;
}
.sys-log-item .msg.info { color: #8eb5ff; }
.sys-log-item .msg.success { color: #00ff00; text-shadow: 0 0 2px rgba(0,255,0,0.3); }
.sys-log-item .msg.warning { color: #ffbf00; }
.sys-log-item .msg.error { color: #ff3333; text-shadow: 0 0 2px rgba(255,51,51,0.3); }

/* 顶部数字面板 */
.number-cards-container {
  display: flex;
  gap: 15px;
  height: 100px;
}
.num-card {
  flex: 1;
  background: rgba(0, 20, 50, 0.6);
  border: 1px solid rgba(0, 150, 255, 0.2);
  display: flex;
  flex-direction: column;
  justify-content: center;
  align-items: center;
  position: relative;
}
.num-title {
  color: #8eb5ff;
  font-size: 14px;
  margin-bottom: 5px;
}
.num-value {
  font-size: 32px;
  font-weight: bold;
}
.neon-text-blue { color: #00f8ff; text-shadow: 0 0 10px #00f8ff; }
.neon-text-green { color: #00ff00; text-shadow: 0 0 10px #00ff00; }
.neon-text-red { color: #ff3333; text-shadow: 0 0 10px #ff3333; }

/* 控制输入区域 */
.control-content {
  display: flex;
  flex-direction: column;
  gap: 20px;
}
.input-group {
  display: flex;
  gap: 10px;
}
.tech-input {
  flex: 1;
  background: rgba(0, 20, 50, 0.6);
  border: 1px solid #00f8ff;
  color: #fff;
  padding: 8px 10px;
  outline: none;
}
.tech-input:focus {
  box-shadow: 0 0 10px rgba(0, 248, 255, 0.5) inset;
}
.tech-input:disabled {
  border-color: #444;
  background: rgba(20, 20, 20, 0.4);
}
.action-row {
  margin-top: 10px;
}

/* 列表过渡动画 - 完美解决元素下掉和重叠 bug */
.fade-move,
.fade-enter-active, 
.fade-leave-active { 
  transition: all 0.5s ease; 
}
.fade-enter-from, 
.fade-leave-to { 
  opacity: 0; 
  transform: translateX(-30px); 
}
.fade-leave-active { 
  position: absolute; 
  left: 0;
  right: 14px; /* account for scrollbar width */
}

/* 强制重做盒模型防溢出 */
*, *::before, *::after {
  box-sizing: border-box;
}
</style>
