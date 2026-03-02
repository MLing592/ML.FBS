<template>
  <div class="dashboard-container">
    <header class="dashboard-header">
      <h1>Industrial Data Dashboard</h1>
      <div :class="['connection-status', isConnected ? 'connected' : 'disconnected']">
        {{ isConnected ? 'Connected' : 'Disconnected' }}
      </div>
    </header>

    <div class="dashboard-content">
      <!-- Alerts Panel -->
      <section class="panel glass-panel">
        <h2><i class="icon-warning"></i> Temperature Warnings</h2>
        <div class="alerts-list">
          <transition-group name="list">
            <div v-for="(alert, index) in temperatureAlerts" :key="alert.time + index" class="alert-item warning">
              <span class="time">{{ alert.time }}</span>
              <span class="msg">Device <strong>{{ alert.deviceId }}</strong> Temp: {{ alert.temp }}°C</span>
            </div>
          </transition-group>
          <div v-if="temperatureAlerts.length === 0" class="empty-state">No active warnings</div>
        </div>
      </section>

      <!-- Machine State Panel -->
      <section class="panel glass-panel">
        <h2><i class="icon-state"></i> Machine States</h2>
        <div class="state-list">
          <transition-group name="list">
            <div v-for="(state, index) in machineStates" :key="state.time + index" class="state-item">
              <span class="time">{{ state.time }}</span>
              <span class="code">[{{ state.code }}]</span> 
              <span class="desc">{{ state.description }}</span>
            </div>
          </transition-group>
          <div v-if="machineStates.length === 0" class="empty-state">Waiting for state updates...</div>
        </div>
      </section>

      <!-- Controls Panel -->
      <section class="panel glass-panel">
        <h2><i class="icon-control"></i> Control Center</h2>
        <div class="control-actions">
          <input type="text" v-model="logMessage" placeholder="Enter log message..." class="modern-input" />
          <button @click="sendLog" class="modern-button primary" :disabled="!isConnected || !logMessage">
            Send Log to Server
          </button>
        </div>
        <div class="control-actions" style="margin-top: 15px;">
           <button @click="triggerAlarm" class="modern-button danger">
            Trigger Remote Alarm
          </button>
        </div>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue';
import * as signalR from '@microsoft/signalr';

const isConnected = ref(false);
const temperatureAlerts = ref<{time: string, deviceId: string, temp: number}[]>([]);
const machineStates = ref<{time: string, code: number, description: string}[]>([]);
const logMessage = ref('');

let connection: signalR.HubConnection | null = null;

const formatTime = () => {
  const now = new Date();
  return `${now.getHours().toString().padStart(2, '0')}:${now.getMinutes().toString().padStart(2, '0')}:${now.getSeconds().toString().padStart(2, '0')}`;
};

onMounted(async () => {
  connection = new signalR.HubConnectionBuilder()
    .withUrl('https://localhost:7212/hubs/deviceStatus')
    .withAutomaticReconnect()
    .build();

  connection.on('ReceiveTemperatureWarning', (deviceId: string, temp: number) => {
    temperatureAlerts.value.unshift({ time: formatTime(), deviceId, temp });
    if (temperatureAlerts.value.length > 20) temperatureAlerts.value.pop();
  });

  connection.on('UpdateMachineState', (stateCode: number, description: string) => {
    machineStates.value.unshift({ time: formatTime(), code: stateCode, description });
    if (machineStates.value.length > 30) machineStates.value.pop();
  });

  connection.onreconnecting(() => { isConnected.value = false; });
  connection.onreconnected(() => { isConnected.value = true; });

  try {
    await connection.start();
    isConnected.value = true;
  } catch (err) {
    console.error('SignalR Connection Error: ', err);
  }
});

onUnmounted(() => {
  if (connection) {
    connection.stop();
  }
});

const sendLog = async () => {
  if (connection && isConnected.value && logMessage.value) {
    try {
      await connection.invoke('SendClientLogToServer', logMessage.value);
      logMessage.value = '';
    } catch (err) {
      console.error('Error sending log:', err);
    }
  }
};

const triggerAlarm = async () => {
  try {
    await fetch('https://localhost:7212/api/alarm/trigger', {
      method: 'POST'
    });
  } catch (err) {
    console.error('Error triggering alarm:', err);
  }
};
</script>

<style scoped>
/* Scoped styles for high-end look */
.dashboard-container {
  min-height: 100vh;
  background: linear-gradient(135deg, #0f172a 0%, #1e1b4b 100%);
  color: #e2e8f0;
  font-family: 'Inter', sans-serif;
  padding: 2rem;
  box-sizing: border-box;
}

.dashboard-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 2rem;
  padding-bottom: 1rem;
  border-bottom: 1px solid rgba(255,255,255,0.1);
}

.dashboard-header h1 {
  margin: 0;
  font-size: 2.2rem;
  font-weight: 700;
  background: linear-gradient(to right, #38bdf8, #818cf8);
  background-clip: text;
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
}

.connection-status {
  padding: 0.5rem 1rem;
  border-radius: 9999px;
  font-weight: 600;
  font-size: 0.9rem;
  transition: all 0.3s ease;
}

.connection-status.connected {
  background: rgba(16, 185, 129, 0.2);
  color: #34d399;
  border: 1px solid rgba(52, 211, 153, 0.5);
  box-shadow: 0 0 15px rgba(16, 185, 129, 0.2);
}

.connection-status.disconnected {
  background: rgba(239, 68, 68, 0.2);
  color: #f87171;
  border: 1px solid rgba(248, 113, 113, 0.5);
  box-shadow: 0 0 15px rgba(239, 68, 68, 0.2);
}

.dashboard-content {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(350px, 1fr));
  gap: 2rem;
}

.glass-panel {
  background: rgba(255, 255, 255, 0.03);
  backdrop-filter: blur(10px);
  -webkit-backdrop-filter: blur(10px);
  border: 1px solid rgba(255, 255, 255, 0.05);
  border-radius: 16px;
  padding: 1.5rem;
  box-shadow: 0 4px 30px rgba(0, 0, 0, 0.1);
  transition: transform 0.3s ease, box-shadow 0.3s ease;
}

.glass-panel:hover {
  transform: translateY(-5px);
  box-shadow: 0 10px 40px rgba(0, 0, 0, 0.2);
}

.glass-panel h2 {
  margin-top: 0;
  margin-bottom: 1.5rem;
  font-size: 1.3rem;
  color: #f8fafc;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.alerts-list, .state-list {
  max-height: 400px;
  overflow-y: auto;
  padding-right: 0.5rem;
}

/* Custom Scrollbar */
::-webkit-scrollbar {
  width: 6px;
}
::-webkit-scrollbar-track {
  background: rgba(0,0,0,0.1);
}
::-webkit-scrollbar-thumb {
  background: rgba(255,255,255,0.2);
  border-radius: 3px;
}

.alert-item, .state-item {
  background: rgba(0, 0, 0, 0.2);
  padding: 1rem;
  border-radius: 8px;
  margin-bottom: 0.8rem;
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
  border-left: 4px solid transparent;
}

.alert-item.warning {
  border-left-color: #f59e0b;
}

.state-item {
  border-left-color: #3b82f6;
  flex-direction: row;
  align-items: center;
  flex-wrap: wrap;
}

.time {
  font-size: 0.8rem;
  color: #94a3b8;
}

.msg {
  font-size: 1rem;
  color: #f1f5f9;
}

.code {
  font-weight: bold;
  color: #60a5fa;
  margin-right: 0.5rem;
}

.desc {
  flex: 1;
}

.empty-state {
  text-align: center;
  padding: 2rem;
  color: #64748b;
  font-style: italic;
}

.control-actions {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.modern-input {
  background: rgba(0,0,0,0.2);
  border: 1px solid rgba(255,255,255,0.1);
  padding: 0.8rem 1rem;
  border-radius: 8px;
  color: white;
  font-size: 1rem;
  transition: border-color 0.3s;
}

.modern-input:focus {
  outline: none;
  border-color: #38bdf8;
}

.modern-button {
  padding: 0.8rem 1.5rem;
  border: none;
  border-radius: 8px;
  font-size: 1rem;
  font-weight: 600;
  cursor: pointer;
  transition: all 0.2s;
}

.modern-button.primary {
  background: linear-gradient(to right, #2563eb, #4f46e5);
  color: white;
}

.modern-button.primary:hover:not(:disabled) {
  opacity: 0.9;
  transform: translateY(-2px);
  box-shadow: 0 4px 15px rgba(37, 99, 235, 0.4);
}

.modern-button.danger {
  background: linear-gradient(to right, #dc2626, #b91c1c);
  color: white;
}

.modern-button.danger:hover {
  opacity: 0.9;
  transform: translateY(-2px);
  box-shadow: 0 4px 15px rgba(220, 38, 38, 0.4);
}

.modern-button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
  transform: none;
  background: #475569;
}

/* List Transitions */
.list-enter-active,
.list-leave-active {
  transition: all 0.4s ease;
}
.list-enter-from {
  opacity: 0;
  transform: translateX(-30px);
}
.list-leave-to {
  opacity: 0;
  transform: translateX(30px);
}
</style>
