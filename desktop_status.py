# desktop_status.py - plain-English desktop overlay and detail page. Read-only.
# Scheduled task "\DEG\DEG Desktop Status" starts it hidden: pythonw desktop_status.py --overlay
# A broken source shows a grey "not available" line and never stops the render.
# State, renders, and the detail page go in the runtime folder next to this script.
# Offline check: python desktop_status.py --fixture fixtures/healthy --no-set
import csv, ctypes, datetime as dt, html, io, json, os, re, subprocess, sys, time, glob, traceback
import psutil
from PIL import Image, ImageChops, ImageDraw, ImageFont, ImageFilter

try:
    import winreg
except ImportError:
    winreg = None

# Thresholds. Change a number here, then run python verify.py.
# Green is fine, amber is worth a glance, red needs attention. Grey is not a fault.
THRESHOLDS = {
    # Under this the processor line says Cool and stays green.
    'cpu_cool_c': 60,
    # Under this it says Warm and stays green. This is normal for a 14th-gen desktop under load.
    'cpu_warm_c': 75,
    # Under this it says Hot and turns amber. At or above it, the line goes red (near heavy throttling).
    'cpu_hot_c': 90,
    # 14th-gen chips are sensitive above this. Amber, and only if the temperature line was still green.
    'cpu_vcore_warn_v': 1.5,
    # A sensor file older than this is stale. The board asks the sensor task to start, at most once per kick gap.
    'cpu_sensor_stale_s': 600,
    'sensor_kick_gap_s': 480,
    # Under this the graphics line says Cool and stays green.
    'gpu_cool_c': 65,
    # Under this it says Warm and stays green.
    'gpu_warm_c': 80,
    # Under this it says Hot and turns amber. At or above it, red.
    'gpu_hot_c': 88,
    # Clocks and voltage are shown as read. They do not pick a color.
    # Power draws at the card's own limit are normal during a game or a model.
    # Amber only when draw is above the limit by this fraction.
    'gpu_power_over_limit_ratio': 1.05,
    # Under this percent used, memory says "plenty free".
    'mem_plenty_pct': 60,
    # Under this, still green ("comfortable" until the plenty line, then the same green).
    'mem_warn_pct': 80,
    # Under this, amber ("getting full"). At or above, red ("nearly full").
    'mem_bad_pct': 92,
    # Speed is shown and not colored: the right number depends on the memory kit.
    # DDR4 sits near 1.2 V and DDR5 near 1.1 V. Above this is high for a daily profile, so amber.
    'ram_voltage_warn_v': 1.45,
    # Free space above this percent is green.
    'disk_free_ok_pct': 15,
    # Free space above this percent (and at or under the green line) is amber. At or under it, red.
    'disk_free_warn_pct': 8,
    # SSD wear from Windows: 0 is new, 100 is worn out. At or above this, amber (plan a replacement).
    'disk_wear_warn_pct': 80,
    # At or above this, red.
    'disk_wear_bad_pct': 95,
    # A drive this warm is amber. Case airflow is usually the cause.
    'disk_temp_warn_c': 55,
    # At or above this, red.
    'disk_temp_bad_c': 70,
    # A 0 rpm reading from the sensor file counts as stopped only when the processor is at or above this.
    # Below it, a stopped fan can be an idle fan, so the line stays green.
    # A 0 from Windows (Win32_Fan) is ignored: that class often reports 0 when it does not know the speed.
    'fan_stop_warn_cpu_c': 70,
    'fan_min_rpm': 100,
    # How many days of WHEA, unexpected restarts, disk errors, and application crashes to count.
    'event_window_days': 30,
    # Any unexpected restart, hardware error, or disk error in the window is red.
    # More than this many application crashes is red. Fewer, but not zero, is amber.
    'app_crash_bad_count': 5,
    # Event log, disk health, and memory-module reads are reused for this long so the 2-minute repaint stays quick.
    'slow_read_cache_s': 1800,
    # On for this many days or more is amber. A restart now and then keeps Windows fresh.
    'uptime_warn_days': 30,
    # Health files older than this many minutes turn that program amber.
    'health_stale_min': 10,
    # BIOS names that carry the Intel voltage fix, and the microcode that turns the fix on.
    'chip_bios_prefixes': ('12.01', '12.1'),
    'chip_microcode_min': 0x133,
    # How long to watch process CPU before ranking who is busiest. Short so the repaint stays quick.
    'proc_sample_s': 0.7,
}

COL = {
    'ok': (74, 222, 128),
    'warn': (251, 191, 36),
    'bad': (248, 113, 113),
    'info': (120, 135, 160),
    'off': (80, 90, 108),
    'na': (148, 156, 170),
}
NOWIN = 0x08000000
FONT_DIRS = (
    r'C:\Windows\Fonts',
    '/usr/share/fonts/truetype/dejavu',
    '/usr/share/fonts/truetype/liberation',
)
FONT_FILES = {
    'light': ('segoeuisl.ttf', 'DejaVuSans.ttf', 'LiberationSans-Regular.ttf'),
    'body': ('segoeui.ttf', 'DejaVuSans.ttf', 'LiberationSans-Regular.ttf'),
    'semibold': ('seguisb.ttf', 'DejaVuSans-Bold.ttf', 'LiberationSans-Bold.ttf'),
}

FRIENDLY = {
    'DEG NHL Recorder': ('NHL Recorder', 'Records live NHL prices'),
    'DEG Desk Scanner': ('Desk Scanner', 'Scans the markets'),
    'DEG Maker Queue': ('Maker Queue', 'Watches order queues'),
    'DEG Edge Paper': ('Edge Paper', 'Practice trader, fake money'),
    'DEG NHL Stage0 Daily': ('NHL Stage 0 Daily', 'Daily scorecard for NHL ideas'),
    'DEG Novig Daily': ('Novig Daily', 'Daily Novig data download'),
    'DEG-CpuSensorPawnIO': ('CPU sensor', 'Reads processor temperature'),
}
ORDER = [
    'DEG NHL Recorder', 'DEG Desk Scanner', 'DEG Maker Queue', 'DEG Edge Paper',
    'DEG NHL Stage0 Daily', 'DEG Novig Daily', 'DEG-CpuSensorPawnIO',
]
# Running, not yet run, or "new launch skipped, already running".
SKIPPED_OK = {267009, 267011, 2147946720, 267014, 267008}
VERDICT_NAMES = {
    'nhl-live-dog-v0': 'Live underdog',
    'nhl-terminal-penny-v0': 'End-of-game penny',
    'nhl-live-dog-model-v0': 'Underdog + model',
    'nhl-live-dog-pinn-v0': 'Underdog + Pinnacle',
}
PNAMES = {
    'svchost': 'Windows services', 'msmpeng': 'Windows Defender', 'dwm': 'Windows desktop',
    'explorer': 'File Explorer', 'system': 'Windows itself', 'memory compression': 'Windows itself',
    'chrome': 'Chrome', 'msedge': 'Edge', 'firefox': 'Firefox', 'cursor': 'Cursor', 'claude': 'Claude',
    'code': 'VS Code', 'searchhost': 'Windows Search', 'registry': 'Windows itself',
    'nvdisplay.container': 'NVIDIA driver', 'onedrive': 'OneDrive', 'spotify': 'Spotify',
    'discord': 'Discord', 'wmiprvse': 'Windows services', 'tiworker': 'Windows Update',
    'powershell': 'PowerShell', 'pwsh': 'PowerShell', 'wscript': 'Windows script',
}
GPU_FIELDS = (
    'name', 'temperature.gpu', 'utilization.gpu', 'memory.used', 'memory.total',
    'power.draw', 'power.limit', 'fan.speed', 'clocks.gr', 'clocks.mem', 'clocks.sm',
    'clocks.max.gr', 'clocks.max.mem',
)
LEVEL_RANK = {'ok': 0, 'info': 1, 'off': 1, 'na': 1, 'warn': 2, 'bad': 3}


def script_dir():
    return os.path.dirname(os.path.abspath(__file__))


def user_documents():
    profile = os.environ.get('USERPROFILE') or os.environ.get('HOME') or ''
    return os.path.join(profile, 'Documents')


class Paths:
    def __init__(self, here, runtime, desk, edge, sensor):
        self.here = here
        self.runtime = runtime
        self.render = os.path.join(runtime, 'render')
        self.state = os.path.join(runtime, 'state.json')
        self.html = os.path.join(runtime, 'status.html')
        self.latest = os.path.join(runtime, 'latest-render.png')
        self.error = os.path.join(runtime, 'last-error.txt')
        self.desk = desk
        self.edge = edge
        self.sensor = sensor


def resolve_paths():
    here = script_dir()
    runtime = os.environ.get('DEG_RUNTIME_DIR') or os.path.join(here, 'runtime')
    desk = os.environ.get('DEG_DESK_ROOT') or os.path.join(user_documents(), 'desk-runners')
    edge = os.environ.get('DEG_EDGE_PAPER') or os.path.join(user_documents(), 'edge-paper')
    programdata = os.environ.get('PROGRAMDATA') or r'C:\ProgramData'
    sensor = os.environ.get('DEG_SENSOR_FILE') or os.path.join(programdata, 'DEG', 'cpu-sensor', 'out', 'last.json')
    return Paths(here, runtime, desk, edge, sensor)


def health_path(paths, key):
    if key == 'DEG Desk Scanner':
        return os.path.join(paths.desk, 'desk-scanner', 'research', 'desk', 'health.json')
    if key == 'DEG NHL Recorder':
        return os.path.join(paths.desk, 'nhl-recorder', 'research', 'desk', 'sports', 'health.json')
    return ''


def stop_path(paths, key):
    return {
        'DEG Edge Paper': os.path.join(paths.edge, 'STOPPED.txt'),
        'DEG Desk Scanner': os.path.join(paths.desk, 'desk-scanner', 'STOPPED.txt'),
        'DEG NHL Recorder': os.path.join(paths.desk, 'nhl-recorder', 'STOPPED.txt'),
        'DEG Maker Queue': os.path.join(paths.desk, 'maker-queue', 'STOPPED.txt'),
    }.get(key, '')


def row(label, text, level='ok', more=None):
    return {'label': label, 'text': text, 'level': level, 'more': more or []}


def na(label, reason, more=None):
    return row(label, 'Not available: ' + reason, 'na', more)


def safe(label, fn):
    try:
        result = fn()
        return result if isinstance(result, list) else [result]
    except Exception as exc:
        return [row(label, 'unknown (could not read)', 'info', [f'{type(exc).__name__}: {exc}'])]


def jload(path):
    with open(path, 'r', encoding='utf-8') as handle:
        return json.load(handle)


def ago(now, when_at):
    if when_at is None:
        return 'never'
    seconds = max(0, (now - when_at).total_seconds())
    if seconds < 90:
        return 'just now'
    if seconds < 3600:
        return f'{int(seconds // 60)} min ago'
    if seconds < 86400:
        return f'{int(seconds // 3600)} hr ago'
    return f'{int(seconds // 86400)} days ago'


def when_text(now, when_at):
    if when_at is None:
        return 'not scheduled'
    day_delta = (when_at.date() - now.date()).days
    clock = when_at.strftime('%I:%M %p').lstrip('0')
    if day_delta == 0:
        return clock
    if day_delta == 1:
        return 'tomorrow ' + clock
    return when_at.strftime('%a ') + clock


def utc_to_local(text):
    parsed = dt.datetime.fromisoformat(text.replace('Z', '+00:00'))
    return parsed.astimezone().replace(tzinfo=None)


def parse_st(text):
    for fmt in ('%m/%d/%Y %I:%M:%S %p',):
        try:
            return dt.datetime.strptime(text, fmt)
        except Exception:
            pass
    return None


def temp_text(value):
    number = float(value)
    if abs(number - round(number)) < 0.05:
        return f'{int(round(number))}\u00b0C'
    return f'{number:.1f}\u00b0C'


def as_list(value):
    if value is None:
        return []
    if isinstance(value, list):
        return value
    if isinstance(value, dict):
        return [value]
    raise TypeError('expected a list or object')


def num(value):
    if value is None:
        return None
    text = str(value).strip()
    if text in ('', '[N/A]', 'N/A', '[Not Supported]', 'Not Supported'):
        return None
    try:
        return float(text)
    except ValueError:
        return None


def event_window_ms():
    return int(THRESHOLDS['event_window_days'] * 86400 * 1000)


def xpath_whea():
    return (
        "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and "
        f"TimeCreated[timediff(@SystemTime) <= {event_window_ms()}]]]"
    )


def xpath_kernel_power():
    return (
        "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41) and "
        f"TimeCreated[timediff(@SystemTime) <= {event_window_ms()}]]]"
    )


def xpath_disk():
    # 7 bad block, 51 paging error, 153 disk reset, 157 surprise removal.
    # Provider disk only, and only level 2 (error) or level 3 (warning).
    # Kernel-Boot 153 is information about VBS and is not a disk error.
    return (
        "*[System[Provider[@Name='disk'] and (Level=2 or Level=3) and "
        "(EventID=7 or EventID=51 or EventID=153 or EventID=157) and "
        f"TimeCreated[timediff(@SystemTime) <= {event_window_ms()}]]]"
    )


def xpath_crash():
    return (
        "*[System[Provider[@Name='Application Error'] and (EventID=1000) and "
        f"TimeCreated[timediff(@SystemTime) <= {event_window_ms()}]]]"
    )


def worse(level, new_level):
    if LEVEL_RANK[new_level] > LEVEL_RANK[level]:
        return new_level
    return level


def plural(count, word):
    if count == 1:
        return f'{count} {word}'
    if word.endswith(('s', 'x', 'z', 'ch', 'sh')):
        return f'{count} {word}es'
    return f'{count} {word}s'


def _rpm_from(value):
    if isinstance(value, bool) or value is None:
        return None
    if isinstance(value, (int, float)):
        return float(value)
    if isinstance(value, str):
        match = re.search(r'[\d.]+', value)
        return float(match.group(0)) if match else None
    if isinstance(value, dict):
        for key in ('rpm', 'speed_rpm', 'speed', 'value', 'DesiredSpeed'):
            if key in value:
                return _rpm_from(value[key])
    return None


def fans_from_sensor(sensor):
    if not isinstance(sensor, dict):
        return []
    found = []
    raw = sensor.get('fans')
    if isinstance(raw, list):
        for index, item in enumerate(raw):
            if isinstance(item, dict):
                name = str(item.get('name') or item.get('label') or f'Fan {index + 1}')
                rpm = _rpm_from(item)
                if rpm is not None:
                    found.append({'name': name, 'rpm': rpm})
            else:
                rpm = _rpm_from(item)
                if rpm is not None:
                    found.append({'name': f'Fan {index + 1}', 'rpm': rpm})
    elif isinstance(raw, dict):
        for name, item in raw.items():
            rpm = _rpm_from(item)
            if rpm is not None:
                found.append({'name': str(name), 'rpm': rpm})
    if found:
        return found
    for key, value in sensor.items():
        if key == 'fans' or 'fan' not in str(key).lower():
            continue
        rpm = _rpm_from(value)
        if rpm is not None:
            found.append({'name': str(key).replace('_', ' '), 'rpm': rpm})
    return found


def fans_from_wmi(items):
    found = []
    for index, item in enumerate(as_list(items)):
        if not isinstance(item, dict):
            continue
        rpm = _rpm_from(item.get('DesiredSpeed'))
        # Win32_Fan often returns 0 when it has no reading. That is not a stopped fan.
        if rpm is None or rpm <= 0:
            continue
        found.append({'name': str(item.get('Name') or f'Fan {index + 1}'), 'rpm': rpm})
    return found


def fan_row(snap):
    sensor = snap['sensor']
    data = {} if sensor.get('error') else sensor.get('data') or {}
    cpu_temp = data.get('cpu_temp_c') if isinstance(data, dict) else None
    sensor_fans = fans_from_sensor(data)
    if sensor_fans:
        fans = sensor_fans
    else:
        fans = fans_from_wmi(snap.get('fans_wmi') or [])
    if not fans:
        if snap.get('fans_wmi_error'):
            return na('Fans', 'no fan speed in the CPU sensor file, and the Windows fan list could not be read')
        return na('Fans', 'no fan speed in the CPU sensor file or Windows fan list')
    stopped = []
    parts = []
    for fan in fans:
        rpm = fan['rpm']
        parts.append(f'{fan["name"]} {rpm:.0f} rpm')
        hot = cpu_temp is not None and float(cpu_temp) >= THRESHOLDS['fan_stop_warn_cpu_c']
        if hot and rpm < THRESHOLDS['fan_min_rpm']:
            stopped.append(fan['name'])
    text = ', '.join(parts)
    if stopped:
        names = ', '.join(stopped)
        return row('Fans', f'{names} reads 0 rpm while the processor is warm', 'bad', [text])
    return row('Fans', text, 'ok')


def parse_gpu_query(main_csv, voltage_csv, voltage_error):
    lines = [line for line in (main_csv or '').splitlines() if line.strip()]
    if not lines:
        return {'error': 'nvidia-smi did not return graphics data'}
    cells = next(csv.reader([lines[0]]))
    if len(cells) < len(GPU_FIELDS):
        return {'error': 'nvidia-smi did not return graphics data'}
    values = {name: cells[index].strip() for index, name in enumerate(GPU_FIELDS)}
    voltage = num(voltage_csv.splitlines()[0]) if voltage_csv and voltage_csv.strip() else None
    # nounits voltage, when a driver returns it, is millivolts. A small number is already volts.
    if voltage is not None and voltage > 10:
        voltage = voltage / 1000.0
    return {
        'name': values['name'],
        'temp_c': num(values['temperature.gpu']),
        'util_pct': num(values['utilization.gpu']),
        'mem_used_mib': num(values['memory.used']),
        'mem_total_mib': num(values['memory.total']),
        'power_w': num(values['power.draw']),
        'power_limit_w': num(values['power.limit']),
        'fan_pct': num(values['fan.speed']),
        'clock_gr': num(values['clocks.gr']),
        'clock_mem': num(values['clocks.mem']),
        'clock_sm': num(values['clocks.sm']),
        'clock_max_gr': num(values['clocks.max.gr']),
        'clock_max_mem': num(values['clocks.max.mem']),
        'voltage_v': voltage,
        'voltage_reason': None if voltage is not None else (voltage_error or 'nvidia-smi did not report GPU voltage'),
    }


def gpu_row(snap):
    gpu = snap['gpu']
    if gpu.get('error'):
        return na('Graphics card', gpu['error'], [gpu['detail']] if gpu.get('detail') else None)
    temp = gpu.get('temp_c')
    if temp is None:
        return na('Graphics card', 'nvidia-smi did not return a temperature')
    if temp < THRESHOLDS['gpu_cool_c']:
        word, level = 'Cool', 'ok'
    elif temp < THRESHOLDS['gpu_warm_c']:
        word, level = 'Warm', 'ok'
    elif temp < THRESHOLDS['gpu_hot_c']:
        word, level = 'Hot', 'warn'
    else:
        word, level = 'Too hot', 'bad'
    util = gpu.get('util_pct')
    if util is None:
        busy = 'activity unknown'
    elif util < 10:
        busy = 'idle'
    else:
        busy = f'working at {util:.0f}%'
    used = gpu.get('mem_used_mib')
    total = gpu.get('mem_total_mib')
    if used is None or total is None:
        memory = 'video memory unknown'
    else:
        memory = f'{used / 1024:.1f} of {total / 1024:.0f} GB video memory in use'
    power = gpu.get('power_w')
    limit = gpu.get('power_limit_w')
    clock = gpu.get('clock_gr')
    bits = [f'{word}, {temp_text(temp)}', busy, memory]
    if power is not None:
        bits.append(f'{power:.0f} W')
    if clock is not None:
        bits.append(f'{clock:.0f} MHz')
    more = [gpu.get('name') or 'Graphics card']
    if power is not None and limit is not None:
        more.append(f'Power draw {power:.0f} W of {limit:.0f} W limit')
        if power > limit * THRESHOLDS['gpu_power_over_limit_ratio']:
            level = worse(level, 'warn')
            bits.append('over its power limit')
    elif power is not None:
        more.append(f'Power draw {power:.0f} W')
    else:
        more.append('Power not available: nvidia-smi did not report power')
    clock_bits = []
    if clock is not None:
        max_gr = gpu.get('clock_max_gr')
        clock_bits.append(f'graphics {clock:.0f} MHz' + (f' (max {max_gr:.0f})' if max_gr is not None else ''))
    if gpu.get('clock_mem') is not None:
        clock_bits.append(f'memory {gpu["clock_mem"]:.0f} MHz')
    if gpu.get('clock_sm') is not None:
        clock_bits.append(f'shader {gpu["clock_sm"]:.0f} MHz')
    if clock_bits:
        more.append('Clocks: ' + ', '.join(clock_bits))
    else:
        more.append('Clocks not available: nvidia-smi did not report clocks')
    if gpu.get('voltage_v') is not None:
        more.append(f'Voltage {gpu["voltage_v"]:.3f} V')
    else:
        more.append('Voltage not available: ' + (gpu.get('voltage_reason') or 'nvidia-smi did not report GPU voltage'))
    if gpu.get('fan_pct') is not None:
        more.append(f'Card fan {gpu["fan_pct"]:.0f}%')
    return row('Graphics card', ', '.join(bits), level, more)


def mem_row(snap):
    memory = snap['memory']
    if isinstance(memory, dict) and memory.get('error'):
        return na('Memory', memory['error'])
    percent = memory.get('percent')
    if percent is None and memory.get('total'):
        percent = 100.0 * memory['used'] / memory['total']
    if percent < THRESHOLDS['mem_warn_pct']:
        level = 'ok'
    elif percent < THRESHOLDS['mem_bad_pct']:
        level = 'warn'
    else:
        level = 'bad'
    if percent < THRESHOLDS['mem_plenty_pct']:
        word = 'plenty free'
    elif percent < THRESHOLDS['mem_warn_pct']:
        word = 'comfortable'
    elif percent < THRESHOLDS['mem_bad_pct']:
        word = 'getting full'
    else:
        word = 'nearly full'
    used_gb = memory['used'] / 2 ** 30
    total_gb = memory['total'] / 2 ** 30
    free_gb = memory.get('available', 0) / 2 ** 30
    return row('Memory', f'{used_gb:.0f} of {total_gb:.0f} GB used ({word})', level, [f'{free_gb:.1f} GB still available'])


def _module_speed(module):
    for key in ('ConfiguredClockSpeed', 'Speed'):
        value = num(module.get(key))
        if value and value > 0:
            return int(value)
    return None


def _module_voltage(module):
    value = num(module.get('ConfiguredVoltage'))
    if value and value > 0:
        # Win32_PhysicalMemory reports ConfiguredVoltage in millivolts.
        return value / 1000.0
    return None


def ram_speed_row(snap):
    modules = snap['modules']
    if isinstance(modules, dict) and modules.get('error'):
        return na('Memory speed', modules['error'])
    speeds, volts, more = [], [], []
    for module in modules or []:
        speed = _module_speed(module)
        voltage = _module_voltage(module)
        if speed:
            speeds.append(speed)
        if voltage:
            volts.append(voltage)
        bank = module.get('BankLabel') or module.get('DeviceLocator') or 'Module'
        detail = str(bank)
        if speed:
            detail += f' {speed} MT/s'
        if voltage:
            detail += f' {voltage:.2f} V'
        more.append(detail)
    if not speeds and not volts:
        return na('Memory speed', 'Windows did not report memory speed')
    level = 'ok'
    if speeds:
        unique = []
        for speed in speeds:
            if speed not in unique:
                unique.append(speed)
        text = ' and '.join(f'{speed} MT/s' for speed in unique)
    else:
        text = 'Speed not available: Windows did not report memory speed'
    if volts:
        peak = max(volts)
        text += f', {peak:.2f} V'
        if peak > THRESHOLDS['ram_voltage_warn_v']:
            level = 'warn'
            text += ' (voltage high)'
    else:
        text += '. Voltage not available: Windows did not report memory voltage'
    return row('Memory speed', text, level, more)


def disk_row(snap):
    disk = snap['disk_c']
    if isinstance(disk, dict) and disk.get('error'):
        return na('Storage (C:)', disk['error'])
    free = disk['free']
    total = disk['total'] or 1
    pct_free = 100.0 * free / total
    if pct_free > THRESHOLDS['disk_free_ok_pct']:
        level = 'ok'
    elif pct_free > THRESHOLDS['disk_free_warn_pct']:
        level = 'warn'
    else:
        level = 'bad'
    return row('Storage (C:)', f'{free / 2 ** 30:.0f} GB free of {total / 2 ** 30:.0f} GB', level, [f'{pct_free:.0f}% free'])


def _disk_level(disk, counter):
    level = 'ok'
    health = str(disk.get('HealthStatus') or '').strip().lower()
    op = str(disk.get('OperationalStatus') or '').strip().lower()
    notes = []
    if health in ('unhealthy', 'failed'):
        level = 'bad'
    elif 'predictive' in op:
        level = 'bad'
    elif health == 'warning' or 'degraded' in op:
        level = 'warn'
    elif health in ('', 'unknown'):
        notes.append('Windows did not report a health status for this disk')
        level = 'na'
    if counter:
        wear = num(counter.get('Wear'))
        temperature = num(counter.get('Temperature'))
        uncorrected = (num(counter.get('ReadErrorsUncorrected')) or 0) + (num(counter.get('WriteErrorsUncorrected')) or 0)
        if wear is not None:
            if wear >= THRESHOLDS['disk_wear_bad_pct']:
                level = worse(level, 'bad')
            elif wear >= THRESHOLDS['disk_wear_warn_pct']:
                level = worse(level, 'warn')
        if temperature is not None:
            if temperature >= THRESHOLDS['disk_temp_bad_c']:
                level = worse(level, 'bad')
            elif temperature >= THRESHOLDS['disk_temp_warn_c']:
                level = worse(level, 'warn')
        if uncorrected > 0:
            level = 'bad'
    if level == 'na' and notes:
        return 'na', notes
    return ('ok' if level == 'na' else level), notes


def _counter_more(counter):
    if not counter:
        return ['Wear, temperature, and error counters not available for this disk']
    lines = []
    temperature = num(counter.get('Temperature'))
    wear = num(counter.get('Wear'))
    hours = num(counter.get('PowerOnHours'))
    read_total = num(counter.get('ReadErrorsTotal'))
    write_total = num(counter.get('WriteErrorsTotal'))
    read_bad = num(counter.get('ReadErrorsUncorrected'))
    write_bad = num(counter.get('WriteErrorsUncorrected'))
    if temperature is not None:
        lines.append(f'Temperature {temp_text(temperature)}')
    else:
        lines.append('Temperature not reported')
    if wear is not None:
        lines.append(f'Wear {wear:.0f}%')
    else:
        lines.append('Wear not reported')
    if read_bad is not None or write_bad is not None or read_total is not None or write_total is not None:
        lines.append(
            f'Read errors {read_bad or 0:.0f} uncorrected'
            + (f' of {read_total:.0f}' if read_total is not None else '')
            + f', write errors {write_bad or 0:.0f} uncorrected'
            + (f' of {write_total:.0f}' if write_total is not None else '')
        )
    else:
        lines.append('Read and write errors not reported')
    if hours is not None:
        lines.append(f'Powered on for {hours:.0f} hours')
    return lines


def disk_health_row(snap):
    payload = snap['disk_health']
    if isinstance(payload, dict) and payload.get('error') and 'disks' not in payload:
        return na('Disk health', payload['error'])
    disks = as_list((payload or {}).get('disks'))
    if not disks:
        reason = (payload or {}).get('error') or 'Windows did not list any physical disks'
        return na('Disk health', reason)
    counters = {}
    for counter in as_list((payload or {}).get('reliability')):
        counters[str(counter.get('DeviceId'))] = counter
    levels = []
    more = []
    problems = []
    warmest = None
    wears = []
    for disk in disks:
        counter = counters.get(str(disk.get('DeviceId')))
        level, notes = _disk_level(disk, counter)
        levels.append(level)
        name = disk.get('FriendlyName') or f'Disk {disk.get("DeviceId")}'
        health = disk.get('HealthStatus') or 'status not reported'
        more.append(f'{name}: {health}')
        more.extend(_counter_more(counter))
        more.extend(notes)
        if level in ('warn', 'bad'):
            problems.append(f'{name} {health}'.strip())
        if counter and num(counter.get('Temperature')) is not None:
            warmest = num(counter.get('Temperature')) if warmest is None else max(warmest, num(counter.get('Temperature')))
        if counter and num(counter.get('Wear')) is not None:
            wears.append(num(counter.get('Wear')))
    if payload.get('reliability_error'):
        more.append('Wear and error counters not available: ' + str(payload['reliability_error']))
    level = 'ok'
    for item in levels:
        level = worse(level, item if item != 'na' else 'ok')
    if levels and all(item == 'na' for item in levels):
        return na('Disk health', 'Windows did not report a health status for these disks', more)
    if problems:
        text = '; '.join(problems)
    else:
        text = 'All disks healthy'
        extras = []
        if warmest is not None:
            extras.append(f'Warmest {temp_text(warmest)}')
        if wears:
            extras.append(f'wear {max(wears):.0f}%')
        if extras:
            extra = ', '.join(extras)
            extra = extra[0].upper() + extra[1:]
            text += '. ' + extra
    return row('Disk health', text, level, more)


def _count_phrase(count, word):
    if count >= 500:
        return f'at least {count} {word}' + ('' if count == 1 else 's')
    return plural(count, word)


def stability_row(snap):
    counts = snap['events']
    if isinstance(counts, dict) and counts.get('error'):
        return na('Stability', counts['error'])
    whea = int(counts.get('whea') or 0)
    kp = int(counts.get('kp') or 0)
    disk = int(counts.get('disk') or 0)
    crash = int(counts.get('crash') or 0)
    more = [
        f'Hardware error reports (WHEA) in last 30 days: {whea}',
        f'Unexpected shutdowns (Kernel-Power 41) in last 30 days: {kp}',
        f'Disk errors in last 30 days: {disk}',
        f'Application crashes in last 30 days: {crash}',
        'Checked every 30 min. Unexpected restarts and hardware errors should stay at zero.',
    ]
    problems = []
    level = 'ok'
    if kp:
        problems.append(_count_phrase(kp, 'unexpected restart'))
        level = 'bad'
    if whea:
        problems.append(_count_phrase(whea, 'hardware error'))
        level = 'bad'
    if disk:
        problems.append(_count_phrase(disk, 'disk error'))
        level = 'bad'
    if crash:
        problems.append(_count_phrase(crash, 'application crash'))
        if crash > THRESHOLDS['app_crash_bad_count']:
            level = 'bad'
        elif level == 'ok':
            level = 'warn'
    if not problems:
        return row('Stability', 'No unexpected restarts in 30 days', 'ok', more)
    return row('Stability', _join_problems(problems) + ' in the last 30 days', level, more)


def _join_problems(parts):
    if len(parts) == 1:
        return parts[0]
    if len(parts) == 2:
        return f'{parts[0]} and {parts[1]}'
    return ', '.join(parts[:-1]) + f', and {parts[-1]}'


def chip_row(snap):
    bios = snap['bios']
    micro = snap['microcode']
    bios_text = None if isinstance(bios, dict) else bios
    micro_value = None if isinstance(micro, dict) else micro
    if not bios_text and isinstance(bios, dict):
        return na('Chip protection', bios.get('error') or 'BIOS version could not be read')
    more = []
    if bios_text:
        more.append(f'BIOS version {bios_text}')
    if micro_value is not None:
        more.append(f'Chip microcode 0x{int(micro_value):X}')
    elif isinstance(micro, dict) and micro.get('error'):
        more.append(micro['error'])
    prefixes = THRESHOLDS['chip_bios_prefixes']
    fixed = bool(bios_text) and any(str(bios_text).startswith(prefix) for prefix in prefixes)
    if fixed and micro_value is not None and int(micro_value) >= THRESHOLDS['chip_microcode_min']:
        return row('Chip protection', 'Intel fix installed (safe voltage rules active)', 'ok', more)
    if micro_value is None:
        return na('Chip protection', 'Windows did not report the chip microcode', more)
    return row('Chip protection', 'Intel fix may be missing - needs a look', 'bad', more)


def windows_row(snap):
    pending = snap['reboot_pending']
    if isinstance(pending, dict) and pending.get('error'):
        return na('Windows updates', pending['error'])
    names = set()
    procs = snap.get('procs') or {}
    if isinstance(procs, dict):
        names = procs.get('names') or set()
    installing = any(name in names for name in ('tiworker.exe', 'trustedinstaller.exe'))
    if pending:
        return row('Windows updates', 'Waiting for a restart to finish updating', 'warn')
    if installing:
        return row('Windows updates', 'Installing updates in the background', 'info')
    return row('Windows updates', 'Up to date, nothing waiting', 'ok')


def uptime_row(snap):
    seconds = snap['uptime_s']
    if isinstance(seconds, dict) and seconds.get('error'):
        return na('Running time', seconds['error'])
    days, hours = int(seconds // 86400), int(seconds % 86400 // 3600)
    if days:
        text = f'On for {days} day{"s" if days != 1 else ""}, {hours} hr'
    else:
        text = f'On for {hours} hr {int(seconds % 3600 // 60)} min'
    extra = ['A restart now and then keeps Windows fresh'] if days >= THRESHOLDS['uptime_warn_days'] else []
    level = 'warn' if days >= THRESHOLDS['uptime_warn_days'] else 'ok'
    return row('Running time', text, level, extra)


def read_sensor(path, now, state, kick):
    try:
        data = jload(path)
    except Exception as exc:
        return {'error': f'the CPU sensor file could not be read ({type(exc).__name__})'}
    try:
        stamp = dt.datetime.fromisoformat(str(data['ts'])).astimezone().replace(tzinfo=None)
    except Exception:
        return {'error': 'the CPU sensor file has no readable time'}
    age = (now - stamp).total_seconds()
    started = False
    if age > THRESHOLDS['cpu_sensor_stale_s'] and time.time() - state.get('sensor_kick', 0) > THRESHOLDS['sensor_kick_gap_s']:
        kick()
        state['sensor_kick'] = time.time()
        started = True
    return {'data': data, 'age': age, 'started': started}


def cpu_row(snap):
    sensor = snap['sensor']
    if sensor.get('error'):
        return na('Processor', sensor['error'])
    data = sensor['data']
    now = snap['now']
    age = sensor.get('age') or 0
    temp = data.get('cpu_temp_c')
    if temp is None:
        return na('Processor', 'the CPU sensor file has no temperature')
    temp = float(temp)
    if temp < THRESHOLDS['cpu_cool_c']:
        word, level = 'Cool', 'ok'
    elif temp < THRESHOLDS['cpu_warm_c']:
        word, level = 'Warm', 'ok'
    elif temp < THRESHOLDS['cpu_hot_c']:
        word, level = 'Hot', 'warn'
    else:
        word, level = 'Too hot', 'bad'
    text = f'{word}, {temp_text(temp)}'
    power = data.get('cpu_pkg_power_w')
    if power is not None:
        text += f', using {float(power):.0f} watts'
    cores = data.get('cpu_core_temps_c') or [temp]
    diag = data.get('diag') or {}
    more = [
        f'Hottest core readings: {temp_text(max(cores))} (limit {temp_text(data.get("tjmax_c", 100))})',
        f'Voltage: {data.get("cpu_vcore_v")} V (what the chip asks for; the 14th-gen chip is sensitive to high voltage)',
        f'Power: {power} W (limit {diag.get("pl1_w")} W sustained, {diag.get("pl2_w")} W burst)',
        f'Sensor reading taken {ago(now, now - dt.timedelta(seconds=age))}',
    ]
    if age > THRESHOLDS['cpu_sensor_stale_s']:
        level = 'info'
        stale = ago(now, now - dt.timedelta(seconds=age))
        if sensor.get('started'):
            text = f'Last reading is old ({stale}); restarting the sensor - {temp_text(temp)} then'
        else:
            text = f'Last reading is old ({stale}) - {temp_text(temp)} then'
    vcore = data.get('cpu_vcore_v')
    if vcore is not None and float(vcore) > THRESHOLDS['cpu_vcore_warn_v']:
        text += ', voltage high'
        if level == 'ok':
            level = 'warn'
    return row('Processor', text, level, more)


def tasks_rows(snap):
    raw = snap['tasks_csv']
    if isinstance(raw, dict) and raw.get('error'):
        return [na('Programs', raw['error'])]
    now = snap['now']
    paths = snap['paths']
    reader = csv.DictReader(io.StringIO(raw))
    tasks = {}
    for item in reader:
        name = item.get('TaskName', '')
        if name.startswith('\\DEG\\'):
            tasks[name[5:]] = item
    rows = []
    for key in ORDER:
        nice, what = FRIENDLY[key]
        item = tasks.get(key)
        if item is None:
            rows.append(row(nice, 'Not set up on this PC', 'info', [what]))
            continue
        status, state = item.get('Status'), item.get('Scheduled Task State')
        last, nxt = parse_st(item.get('Last Run Time') or ''), parse_st(item.get('Next Run Time') or '')
        try:
            result = int(item.get('Last Result')) & 0xFFFFFFFF
        except Exception:
            result = None
        more = [what, f'Last started: {ago(now, last)} ({item.get("Last Run Time")})', f'Next: {when_text(now, nxt)}', f'Result code: {result}']
        stopped = os.path.exists(stop_path(paths, key))
        if stopped:
            rows.append(row(nice, 'Paused on purpose (stop note found)', 'off', more))
            continue
        if state == 'Disabled':
            rows.append(row(nice, 'Switched off', 'off', more))
            continue
        level, text = 'ok', ''
        if status == 'Running':
            text = 'Running'
        elif result == 0 or result in SKIPPED_OK:
            text = f'Idle, last ran {ago(now, last)}' + (f', next {when_text(now, nxt)}' if nxt else '')
        else:
            level, text = 'warn', f'Last run had a problem ({ago(now, last)})'
        health = health_path(paths, key)
        if health:
            try:
                doc = jload(health)
                stamp = doc.get('last_run_ts') or doc.get('last_poll_ts')
                seen = utc_to_local(stamp)
                mins = (now - seen).total_seconds() / 60
                more.append(f'Health file says last activity {ago(now, seen)}')
                if mins > THRESHOLDS['health_stale_min']:
                    level, text = 'warn', f'{text}, but no fresh data for {int(mins)} min'
                else:
                    text += f', data fresh ({ago(now, seen)})'
                gaps = doc.get('gaps_last_24h', doc.get('tape_gaps_last_24h'))
                err = doc.get('errors_last_24h')
                limited = doc.get('requests_429_last_hour')
                if err:
                    more.append(f'{err} hiccups in the last 24 hours')
                if gaps:
                    more.append(f'{gaps} gaps in the last 24 hours')
                    if level == 'ok':
                        level = 'warn'
                if limited:
                    more.append(f'Rate-limited {limited} times in the last hour')
                    if level == 'ok':
                        level = 'warn'
                saved = doc.get('scan_bytes_today', doc.get('bytes_today'))
                if saved:
                    more.append(f'Saved {saved / 1e6:.0f} MB today')
            except Exception as exc:
                text += ' (health file unreadable)'
                more.append(type(exc).__name__)
                if level == 'ok':
                    level = 'info'
        rows.append(row(nice, text, level, more))
    return rows


def stage0_rows(snap):
    path = os.path.join(snap['paths'].desk, 'logs', 'nhl-stage0-daily.log')
    with open(path, 'rb') as handle:
        handle.seek(0, 2)
        size = handle.tell()
        handle.seek(max(0, size - 12000))
        text = handle.read().decode('utf-8', 'replace')
    starts = [match.start() for match in re.finditer(r'^==== .* start', text, re.M)]
    block = text[starts[-1]:] if starts else text
    when_s = re.search(r'^==== (\S+ \S+) start', block, re.M)
    rows = []
    for line in block.splitlines():
        if not line.startswith('nhl-'):
            continue
        tokens = line.split()
        if len(tokens) < 10:
            continue
        name, fills, games, days = tokens[0], tokens[1], tokens[2], tokens[3]
        verdict = ' '.join(tokens[9:])
        nice = VERDICT_NAMES.get(name, name)
        more = [
            f'{name}: {fills} practice trades over {games} games / {days} days',
            f'Average result {tokens[4]} cents (90% range {tokens[5]} to {tokens[6]})',
            f'Verdict from log: {verdict}',
        ]
        upper = verdict.upper()
        if upper.startswith('NOT YET'):
            rows.append(row(nice, f'Not enough data yet ({fills} trades so far)', 'info', more))
        elif upper.startswith(('PASS', 'GO', 'YES')):
            rows.append(row(nice, f'Passed ({fills} trades)', 'ok', more))
        else:
            rows.append(row(nice, f'{verdict} ({fills} trades)', 'warn', more))
    if not rows:
        raise ValueError('no verdict table found')
    if when_s:
        rows[0]['more'].append(f'Scorecard from {when_s.group(1)}')
    return rows


def paper_row(snap):
    reports = glob.glob(os.path.join(snap['paths'].edge, 'reports', '*.md'))
    if not reports:
        return row('Practice trader', 'No report yet', 'info')
    latest = max(reports, key=os.path.getmtime)
    text = open(latest, encoding='utf-8', errors='replace').read()
    pnl = re.search(r'PnL \(dollars\):\s*(-?[\d.]+)', text)
    fills = re.search(r'Paper fills:\s*(\d+)', text)
    if not pnl:
        return row('Practice trader', 'Report has no result line', 'info', [os.path.basename(latest)])
    value = float(pnl.group(1))
    direction = 'up' if value >= 0 else 'down'
    phrase = f'Fake money: {direction} ${abs(value):.2f}'
    if fills:
        phrase += f' on {fills.group(1)} practice trades'
    return row('Practice trader', phrase, 'info', [f'Latest report: {os.path.basename(latest)}', 'Paper trading only. No real money moves'])


def friendly_proc(proc):
    name = proc.info.get('name') or '?'
    base = re.sub(r'\.exe$', '', name, flags=re.I).lower()
    if base in ('python', 'pythonw', 'node'):
        try:
            cmd = ' '.join(proc.cmdline()).lower()
        except Exception:
            cmd = ''
        for key, label in (
            ('desk-scanner', 'Desk Scanner'), ('nhl-recorder', 'NHL Recorder'),
            ('maker-queue', 'Maker Queue'), ('edge-machine', 'Edge Paper'),
            ('openclaw', 'OpenClaw'), ('local-ai-bench', 'AI benchmark'),
            ('desktop-status', 'This status display'), ('novig', 'Novig download'),
        ):
            if key in cmd:
                return label
        return 'Python' if base.startswith('python') else 'Node.js'
    return PNAMES.get(base, re.sub(r'[-_]', ' ', base).title())


def _cmdline(proc):
    try:
        return proc.cmdline()
    except Exception:
        return []


def inspect_procs(host):
    procs = list(host.process_iter())
    for proc in procs:
        try:
            proc.cpu_percent(None)
        except Exception:
            pass
    host.pause(THRESHOLDS['proc_sample_s'])
    cpu, mem, names = {}, {}, set()
    ncpu = host.cpu_count() or 1
    local, benches, openclaw = [], False, False
    for proc in procs:
        try:
            label = friendly_proc(proc)
            share = proc.cpu_percent(None) / ncpu
            rss = proc.memory_info().rss
            low = (proc.info.get('name') or '').lower()
            cmd = ' '.join(_cmdline(proc)).lower()
            if label == 'System Idle Process':
                continue
            cpu[label] = cpu.get(label, 0) + share
            mem[label] = mem.get(label, 0) + rss
            names.add(low)
            if low in ('llama-server.exe', 'llama-bench.exe', 'ollama.exe', 'ollama app.exe'):
                local.append(low.replace('.exe', ''))
            if low.startswith('python') and 'local-ai-bench' in cmd:
                benches = True
            if low == 'node.exe' and 'openclaw' in cmd:
                openclaw = True
        except Exception:
            pass
    return {'cpu': cpu, 'mem': mem, 'names': names, 'local_ai': local, 'benches': benches, 'openclaw': openclaw}


def busy_rows(snap):
    procs = snap.get('procs') or {}
    if procs.get('error') and not procs.get('cpu'):
        return [na('Using the most power', 'process use could not be read')]
    cpu, mem = procs.get('cpu') or {}, procs.get('mem') or {}

    def top(mapping, count=3):
        return sorted(mapping.items(), key=lambda item: -item[1])[:count]

    cpu_text = ', '.join(f'{name} {value:.0f}%' if value >= 1 else f'{name} under 1%' for name, value in top(cpu)) or 'Nothing to report'
    mem_text = ', '.join(f'{name} {value / 2 ** 30:.1f} GB' for name, value in top(mem)) or 'Nothing to report'
    return [
        row('Using the most power', cpu_text, 'info', [f'{name}: {value:.1f}% of the processor' for name, value in top(cpu, 8)]),
        row('Using the most memory', mem_text, 'info', [f'{name}: {value / 2 ** 30:.2f} GB' for name, value in top(mem, 8)]),
    ]


def ai_rows(snap):
    procs = snap.get('procs') or {}
    if procs.get('error') and not procs.get('names'):
        return [na('Local AI models', 'process list could not be read')]
    names = procs.get('names') or set()
    local = list(procs.get('local_ai') or [])
    if procs.get('benches'):
        local.append('benchmark script')
    rows = []
    if local:
        rows.append(row('Local AI models', 'Working now: ' + ', '.join(local), 'ok'))
    else:
        rows.append(row('Local AI models', 'Nothing running right now', 'info'))
    rows.append(row('Claude Code', 'Running' if 'claude.exe' in names else 'Not open', 'ok' if 'claude.exe' in names else 'info'))
    rows.append(row('Cursor', 'Open' if 'cursor.exe' in names else 'Not open', 'ok' if 'cursor.exe' in names else 'info'))
    openclaw = bool(procs.get('openclaw'))
    rows.append(row('OpenClaw (Telegram bot)', 'Running, listening for messages' if openclaw else 'Not running', 'ok' if openclaw else 'warn'))
    return rows


def headline(sections):
    bad = [item['label'] for _, rows in sections for item in rows if item['level'] == 'bad']
    warn = [item['label'] for _, rows in sections for item in rows if item['level'] == 'warn']
    missing = [item['label'] for _, rows in sections for item in rows if item['level'] == 'na']
    if bad:
        return 'Needs a look: ' + ', '.join(bad[:3]), 'bad'
    if warn:
        return 'Mostly good. Keep an eye on: ' + ', '.join(warn[:3]), 'warn'
    if missing:
        return 'All good on the readings we have', 'ok'
    return 'All good', 'ok'


def tag_source(items, source):
    for item in items:
        if not item.get('source'):
            item['source'] = source
    return items


def gather(snap):
    sections = []
    computer = []
    for label, fn, source in (
        ('Processor', cpu_row, 'CPU sensor file'),
        ('Graphics card', gpu_row, 'nvidia-smi'),
        ('Fans', fan_row, 'CPU sensor file or Windows fan list'),
        ('Memory', mem_row, 'Windows memory'),
        ('Memory speed', ram_speed_row, 'Win32_PhysicalMemory'),
        ('Storage', disk_row, 'disk free space'),
        ('Disk health', disk_health_row, 'Windows disk health'),
        ('Stability', stability_row, 'Windows event log'),
        ('Chip protection', chip_row, 'BIOS and microcode'),
        ('Windows updates', windows_row, 'Windows update status'),
        ('Running time', uptime_row, 'system uptime'),
    ):
        computer += tag_source(safe(label, lambda fn=fn: fn(snap)), source)
    sections.append(('Your computer', computer))
    sections.append(('Your programs', tag_source(safe('Programs', lambda: tasks_rows(snap)), 'scheduled tasks')))
    research = tag_source(safe('Scorecard', lambda: stage0_rows(snap)), 'Stage 0 scorecard')
    research += tag_source(safe('Practice trader', lambda: paper_row(snap)), 'practice trader report')
    sections.append(('Research', research))
    sections.append(('AI and assistants', tag_source(safe('AI', lambda: ai_rows(snap)), 'process list')))
    sections.append(('Busiest right now', tag_source(safe('Busy', lambda: busy_rows(snap)), 'process list')))
    return sections


def font(kind, size):
    for folder in FONT_DIRS:
        for name in FONT_FILES[kind]:
            path = os.path.join(folder, name)
            if os.path.exists(path):
                try:
                    return ImageFont.truetype(path, size)
                except Exception:
                    pass
    return ImageFont.load_default()


def fit(draw, text, face, maxw):
    if draw.textlength(text, font=face) <= maxw:
        return text
    while text and draw.textlength(text + '\u2026', font=face) > maxw:
        text = text[:-1]
    return text + '\u2026'


def dot(img, cx, cy, radius, color):
    scale = 4
    size = radius * 2 * scale + 8 * scale
    stamp = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    pen = ImageDraw.Draw(stamp)
    center = stamp.width // 2
    pen.ellipse((center - radius * scale * 1.9, center - radius * scale * 1.9, center + radius * scale * 1.9, center + radius * scale * 1.9), fill=color + (40,))
    pen.ellipse((center - radius * scale, center - radius * scale, center + radius * scale, center + radius * scale), fill=color + (255,))
    stamp = stamp.resize((stamp.width // scale, stamp.height // scale), Image.LANCZOS)
    img.alpha_composite(stamp, (int(cx - stamp.width / 2), int(cy - stamp.height / 2)))


def render_size():
    raw = os.environ.get('DEG_RENDER_SIZE')
    if raw and 'x' in raw.lower():
        width, height = raw.lower().split('x', 1)
        return int(width), int(height)
    return 3440, 1440


def render_png(sections, now, computer, size=None):
    width, height = size or render_size()
    bg = Image.new('RGBA', (width, height))
    pixels = ImageDraw.Draw(bg)
    for y in range(height):
        blend = y / max(1, height)
        pixels.line([(0, y), (width, y)], fill=(int(11 + 8 * blend), int(16 + 9 * blend), int(27 + 14 * blend), 255))
    blur = os.environ.get('DEG_RENDER_BLUR', '1') != '0'
    if blur:
        glow = Image.new('RGBA', (width, height), (0, 0, 0, 0))
        glow_draw = ImageDraw.Draw(glow)
        glow_draw.ellipse((width - 1500, -500, width + 300, 900), fill=(40, 70, 140, 70))
        glow_draw.ellipse((-400, height - 600, 1100, height + 500), fill=(30, 90, 90, 40))
        bg.alpha_composite(glow.filter(ImageFilter.GaussianBlur(min(200, max(8, width // 17)))))
    margin = 44 if width >= 1600 else 16
    panel_w = 1330 if width >= 3000 else min(1330, width - margin * 2)
    panel_x = width - panel_w - margin
    panel_y = 28 if height >= 900 else 12
    panel_h = height - panel_y - (78 if height >= 900 else 24)
    panel = Image.new('RGBA', (width, height), (0, 0, 0, 0))
    panel_draw = ImageDraw.Draw(panel)
    panel_draw.rounded_rectangle((panel_x, panel_y, panel_x + panel_w, panel_y + panel_h), 28, fill=(18, 25, 40, 205), outline=(255, 255, 255, 22), width=2)
    bg.alpha_composite(panel)
    draw = ImageDraw.Draw(bg)
    head, level = headline(sections)
    x0 = panel_x + min(48, max(16, panel_w // 20))
    dot(bg, x0 + 14, panel_y + 70, 13, COL[level])
    draw.text((x0 + 44, panel_y + 30), fit(draw, head, font('light', 58), panel_w - 150), font=font('light', 58), fill=(240, 245, 252))
    sub = f'{computer}  \u00b7  updated {now.strftime("%I:%M %p").lstrip("0")}  \u00b7  click "PC Status" on the desktop for details'
    draw.text((x0 + 44, panel_y + 102), fit(draw, sub, font('body', 22), panel_w - 120), font=font('body', 22), fill=(128, 144, 170))
    nrows = sum(len(rows) for _, rows in sections)
    top = panel_y + 168
    avail = panel_y + panel_h - top - 20
    sec_h = 52 if avail > 400 else 28
    row_h = min(37, (avail - sec_h * len(sections)) / max(1, nrows))
    if row_h < 12:
        row_h = 12
    font_size = int(min(24, max(10, row_h * 0.66)))
    face_label, face_text, face_sec = font('semibold', font_size), font('body', font_size), font('semibold', 20 if sec_h > 36 else 14)
    y = top
    for title, rows in sections:
        draw.text((x0, y + 14), title.upper(), font=face_sec, fill=(112, 140, 190))
        draw.line([(x0 + draw.textlength(title.upper(), font=face_sec) + 18, y + 27), (panel_x + panel_w - 48, y + 27)], fill=(255, 255, 255, 24), width=1)
        y += sec_h
        for item in rows:
            dot(bg, x0 + 14, y + row_h / 2, 8 if row_h > 30 else 5, COL[item['level']])
            label_w = min(330, int(panel_w * 0.28))
            draw.text((x0 + 44, y + max(0, (row_h - font_size * 1.35) / 2)), fit(draw, item['label'], face_label, label_w), font=face_label, fill=(225, 232, 244))
            dim = item['level'] == 'off'
            draw.text((x0 + label_w + 24, y + max(0, (row_h - font_size * 1.35) / 2)), fit(draw, item['text'], face_text, panel_w - label_w - 120), font=face_text, fill=(120, 132, 152) if dim else (170, 184, 206))
            y += row_h
    return bg.convert('RGB')


def render_html(sections, now, computer):
    head, level = headline(sections)
    colors = {key: '#%02x%02x%02x' % value for key, value in COL.items()}
    parts = [f'''<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="refresh" content="120"><title>PC Status</title><style>
:root{{--bg:#fff;--fg:#1a2233;--mut:#5b6a82;--line:#e3e8f0}}
@media(prefers-color-scheme:dark){{:root{{--bg:#0f1521;--fg:#e6ecf5;--mut:#8696b0;--line:#222c40}}}}
body{{margin:0;background:var(--bg);color:var(--fg);font:16px/1.5 "Segoe UI Variable","Segoe UI",system-ui,sans-serif}}
main{{max-width:880px;margin:0 auto;padding:40px 20px 80px}}
h1{{font-weight:600;font-size:34px;margin:0 0 4px;display:flex;align-items:center;gap:14px}}
h2{{font-size:13px;letter-spacing:.09em;text-transform:uppercase;color:var(--mut);margin:38px 0 8px;font-weight:600}}
.sub{{color:var(--mut);margin:0}}
.d{{display:inline-block;width:12px;height:12px;border-radius:50%;flex:none}}
.r{{display:grid;grid-template-columns:18px 210px 1fr;gap:6px 12px;padding:10px 0;border-top:1px solid var(--line)}}
.r b{{font-weight:600}} .r .m{{grid-column:3;color:var(--mut);font-size:14px}}
.r .d{{margin-top:6px}}
@media(max-width:620px){{.r{{grid-template-columns:18px 1fr}}.r span.t,.r .m{{grid-column:2}}}}
</style></head><body><main>
<h1><span class="d" style="background:{colors[level]};width:18px;height:18px"></span>{html.escape(head)}</h1>
<p class="sub">{html.escape(computer)}. Updated {now.strftime("%A %I:%M %p").replace(" 0", " ")}. This page refreshes itself every 2 minutes.</p>''']
    for title, rows in sections:
        parts.append(f'<h2>{html.escape(title)}</h2>')
        for item in rows:
            parts.append(
                f'<div class="r"><span class="d" style="background:{colors[item["level"]]}"></span>'
                f'<b>{html.escape(item["label"])}</b><span class="t">{html.escape(item["text"])}</span>'
            )
            for extra in item['more']:
                parts.append(f'<div class="m">{html.escape(str(extra))}</div>')
            source = item.get('source') or ''
            if source:
                parts.append(f'<div class="m">Source: {html.escape(source)}</div>')
            parts.append('</div>')
    unknown = [item['label'] for _, rows in sections for item in rows if item['text'].startswith('unknown')]
    if unknown:
        parts.append('<h2>Could not read</h2><p>' + html.escape(', '.join(unknown)) + '</p>')
    parts.append('<p class="sub" style="margin-top:40px">Green = fine. Amber = worth a glance. Red = needs attention. Grey = not available, just information, or switched off.</p></main></body></html>')
    return '\n'.join(parts)


def set_wallpaper(path):
    ctypes.windll.user32.SystemParametersInfoW(0x0014, 0, path, 3)


def _powershell_exe():
    return 'powershell.exe' if os.name == 'nt' else 'powershell'


def _creation_kwargs():
    if os.name == 'nt':
        return {'creationflags': NOWIN}
    return {}


class LiveHost:
    built = 0

    def __init__(self):
        LiveHost.built += 1
        self.commands = []

    def computer_name(self):
        return os.environ.get('COMPUTERNAME') or 'This PC'

    def clock(self):
        return dt.datetime.now()

    def run(self, cmd, timeout=20):
        self.commands.append([str(part) for part in cmd])
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, errors='replace', **_creation_kwargs())
        if proc.returncode != 0:
            detail = (proc.stderr or proc.stdout or '').strip()
            raise RuntimeError(detail or f'{cmd[0]} failed')
        return proc.stdout

    def pause(self, seconds):
        time.sleep(seconds)

    def process_iter(self):
        return psutil.process_iter(['name'])

    def cpu_count(self):
        return psutil.cpu_count() or 1

    def memory(self):
        mem = psutil.virtual_memory()
        return {'used': mem.used, 'total': mem.total, 'available': mem.available, 'percent': mem.percent}

    def disk_c(self):
        usage = psutil.disk_usage('C:\\' if os.name == 'nt' else '/')
        return {'free': usage.free, 'total': usage.total}

    def uptime_s(self):
        return time.time() - psutil.boot_time()

    def gpu(self):
        query = ','.join(GPU_FIELDS)
        main = self.run(['nvidia-smi', f'--query-gpu={query}', '--format=csv,noheader,nounits'], 15)
        voltage_csv, voltage_error = '', None
        try:
            voltage_csv = self.run(['nvidia-smi', '--query-gpu=voltage.graphics', '--format=csv,noheader,nounits'], 10)
        except Exception:
            voltage_error = 'nvidia-smi did not report GPU voltage'
        return parse_gpu_query(main, voltage_csv, voltage_error)

    def _ps_json(self, script):
        proc = subprocess.run(
            [_powershell_exe(), '-NoProfile', '-NonInteractive', '-Command', '-'],
            input=script, capture_output=True, text=True, timeout=40, errors='replace', **_creation_kwargs(),
        )
        if proc.returncode != 0:
            raise RuntimeError((proc.stderr or proc.stdout or 'PowerShell failed').strip())
        text = proc.stdout.strip()
        if not text:
            return None
        return json.loads(text)

    def fans_wmi(self):
        payload = self._ps_json(
            "Get-CimInstance Win32_Fan | Select-Object Name, DesiredSpeed, Status | ConvertTo-Json -Compress"
        )
        return as_list(payload)

    def disk_health(self):
        script = r'''
$ErrorActionPreference = 'Stop'
try {
  $disks = @(Get-PhysicalDisk | Select-Object DeviceId, FriendlyName, HealthStatus, @{n='OperationalStatus';e={@($_.OperationalStatus) -join ', '}}, MediaType)
} catch {
  @{ error = $_.Exception.Message } | ConvertTo-Json -Compress
  exit 0
}
try {
  $rel = @(Get-StorageReliabilityCounter | Select-Object DeviceId, Temperature, Wear, ReadErrorsTotal, ReadErrorsUncorrected, WriteErrorsTotal, WriteErrorsUncorrected, PowerOnHours)
} catch {
  @{ disks = $disks; reliability = @(); reliability_error = $_.Exception.Message } | ConvertTo-Json -Depth 4 -Compress
  exit 0
}
@{ disks = $disks; reliability = $rel } | ConvertTo-Json -Depth 4 -Compress
'''
        payload = self._ps_json(script)
        return payload or {'error': 'Windows did not return disk health counters'}

    def modules(self):
        payload = self._ps_json(
            "Get-CimInstance Win32_PhysicalMemory | Select-Object BankLabel, DeviceLocator, Speed, ConfiguredClockSpeed, ConfiguredVoltage | ConvertTo-Json -Compress"
        )
        return as_list(payload)

    def event_count(self, log, xpath):
        out = self.run(['wevtutil', 'qe', log, f'/q:{xpath}', '/c:500', '/f:xml'], 40)
        return out.count('<Event ')

    def bios(self):
        out = self.run([_powershell_exe(), '-NoProfile', '-NonInteractive', '-Command', '(Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion'], 20)
        text = out.strip()
        if not text:
            raise RuntimeError('empty BIOS version')
        return text

    def microcode(self):
        if winreg is None:
            raise OSError('winreg is not available')
        key = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r'HARDWARE\DESCRIPTION\System\CentralProcessor\0')
        try:
            raw = winreg.QueryValueEx(key, 'Update Revision')[0]
        finally:
            winreg.CloseKey(key)
        if isinstance(raw, bytes):
            return int.from_bytes(raw[:4], 'little')
        return int(raw)

    def reboot_pending(self):
        if winreg is None:
            raise OSError('winreg is not available')
        pending = False
        for path in (
            r'SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired',
            r'SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending',
        ):
            try:
                handle = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, path)
                winreg.CloseKey(handle)
                pending = True
            except OSError:
                pass
        return pending

    def tasks_csv(self):
        return self.run(['schtasks', '/query', '/fo', 'csv', '/v'], 40)


class FakeProc:
    def __init__(self, spec, pid):
        self.info = {'name': spec.get('name') or '?'}
        self.pid = pid
        self._cpu = float(spec.get('cpu') or 0)
        self._rss = int(spec.get('rss') or 0)
        self._cmd = list(spec.get('cmd') or [self.info['name']])

    def cpu_percent(self, interval=None):
        return self._cpu

    def memory_info(self):
        return type('Mem', (), {'rss': self._rss})()

    def cmdline(self):
        return list(self._cmd)


class FixtureHost:
    def __init__(self, folder, board):
        self.folder = folder
        self.board = board
        self.commands = []

    def computer_name(self):
        return self.board.get('computer_name') or 'FIXTURE-PC'

    def clock(self):
        return dt.datetime.fromisoformat(self.board['now'])

    def run(self, cmd, timeout=20):
        recorded = [str(part) for part in cmd]
        self.commands.append(recorded)
        if recorded and recorded[0] == 'schtasks':
            return ''
        raise RuntimeError('fixture blocked a command: ' + ' '.join(recorded))

    def pause(self, seconds):
        return None

    def process_iter(self):
        return [FakeProc(spec, index + 1) for index, spec in enumerate(self.board.get('processes') or [])]

    def cpu_count(self):
        return int(self.board.get('cpu_count') or 1)

    def memory(self):
        return dict(self.board['memory'])

    def disk_c(self):
        return dict(self.board['disk_c'])

    def uptime_s(self):
        return float(self.board['uptime_s'])

    def gpu(self):
        if self.board.get('gpu_error'):
            return {'error': self.board['gpu_error']}
        return parse_gpu_query(self.board.get('gpu_csv') or '', self.board.get('voltage_csv') or '', self.board.get('voltage_error'))

    def fans_wmi(self):
        if self.board.get('fans_wmi_error'):
            raise RuntimeError(self.board['fans_wmi_error'])
        return self.board.get('fans_wmi') or []

    def disk_health(self):
        if self.board.get('disk_health_error'):
            return {'error': self.board['disk_health_error']}
        return self.board.get('disk_health') or {'error': 'Windows did not return disk health counters'}

    def modules(self):
        if self.board.get('modules_error'):
            raise RuntimeError(self.board['modules_error'])
        return self.board.get('modules') or []

    def event_count(self, log, xpath):
        if self.board.get('events_error'):
            raise RuntimeError(self.board['events_error'])
        events = self.board.get('events') or {}
        if 'WHEA-Logger' in xpath:
            return int(events.get('whea') or 0)
        if 'Kernel-Power' in xpath:
            return int(events.get('kp') or 0)
        if 'Application Error' in xpath:
            return int(events.get('crash') or 0)
        if 'EventID=7' in xpath:
            return int(events.get('disk') or 0)
        raise RuntimeError('fixture has no count for this event query')

    def bios(self):
        if self.board.get('bios_error'):
            raise RuntimeError(self.board['bios_error'])
        return self.board.get('bios') or ''

    def microcode(self):
        if self.board.get('microcode_error'):
            raise RuntimeError(self.board['microcode_error'])
        if 'microcode' not in self.board:
            raise RuntimeError('no microcode')
        return int(self.board['microcode'])

    def reboot_pending(self):
        return bool(self.board.get('reboot_pending'))

    def tasks_csv(self):
        with open(os.path.join(self.folder, 'tasks.csv'), encoding='utf-8') as handle:
            return handle.read()


def _cached(state, key, loader):
    hit = state.get(key)
    if isinstance(hit, dict) and 'value' in hit and time.time() - hit.get('at', 0) < THRESHOLDS['slow_read_cache_s']:
        return hit['value']
    value = loader()
    state[key] = {'at': time.time(), 'value': value}
    return value


def load_snapshot(host, paths, state, now=None):
    now = now or host.clock()
    snap = {'now': now, 'paths': paths, 'state': state, 'computer_name': host.computer_name()}

    def kick():
        host.run(['schtasks', '/run', '/tn', r'\DEG\DEG-CpuSensorPawnIO'], 10)

    snap['sensor'] = read_sensor(paths.sensor, now, state, kick)
    try:
        snap['gpu'] = host.gpu()
    except Exception as exc:
        snap['gpu'] = {'error': 'nvidia-smi did not return graphics data', 'detail': f'{type(exc).__name__}: {exc}'}
    try:
        snap['memory'] = host.memory()
    except Exception:
        snap['memory'] = {'error': 'memory use could not be read'}
    try:
        snap['modules'] = _cached(state, 'modules', host.modules)
    except Exception:
        snap['modules'] = {'error': 'Windows did not report memory speed'}
    try:
        snap['disk_c'] = host.disk_c()
    except Exception:
        snap['disk_c'] = {'error': 'free space on C: could not be read'}
    try:
        snap['disk_health'] = _cached(state, 'disk_health', host.disk_health)
    except Exception:
        snap['disk_health'] = {'error': 'Windows did not return disk health counters'}
    snap['fans_wmi'] = []
    snap['fans_wmi_error'] = None
    data = {} if snap['sensor'].get('error') else snap['sensor'].get('data') or {}
    if not fans_from_sensor(data):
        try:
            snap['fans_wmi'] = host.fans_wmi()
        except Exception:
            snap['fans_wmi_error'] = 'the Windows fan list could not be read'
    try:
        snap['events'] = _cached(state, 'stab', lambda: {
            'whea': host.event_count('System', xpath_whea()),
            'kp': host.event_count('System', xpath_kernel_power()),
            'disk': host.event_count('System', xpath_disk()),
            'crash': host.event_count('Application', xpath_crash()),
        })
    except Exception:
        snap['events'] = {'error': 'the Windows event log could not be read'}
    try:
        if state.get('bios'):
            snap['bios'] = state['bios']
        else:
            snap['bios'] = host.bios()
            if snap['bios']:
                state['bios'] = snap['bios']
    except Exception:
        snap['bios'] = {'error': 'BIOS version could not be read'}
    try:
        if 'microcode' in state:
            snap['microcode'] = state['microcode']
        else:
            snap['microcode'] = host.microcode()
            state['microcode'] = snap['microcode']
    except Exception:
        snap['microcode'] = {'error': 'Windows did not report the chip microcode'}
    try:
        snap['reboot_pending'] = host.reboot_pending()
    except Exception:
        snap['reboot_pending'] = {'error': 'Windows update status could not be read'}
    try:
        snap['procs'] = inspect_procs(host)
    except Exception:
        snap['procs'] = {'error': 'process list could not be read', 'cpu': {}, 'mem': {}, 'names': set(), 'local_ai': [], 'benches': False, 'openclaw': False}
    try:
        snap['uptime_s'] = host.uptime_s()
    except Exception:
        snap['uptime_s'] = {'error': 'running time could not be read'}
    try:
        snap['tasks_csv'] = host.tasks_csv()
    except Exception:
        snap['tasks_csv'] = {'error': 'scheduled tasks could not be read'}
    return snap


def apply_fixture_env(folder):
    os.environ['DEG_DESK_ROOT'] = os.path.join(folder, 'desk')
    os.environ['DEG_EDGE_PAPER'] = os.path.join(folder, 'edge-paper')
    os.environ['DEG_SENSOR_FILE'] = os.path.join(folder, 'sensor.json')


def load_state(paths):
    try:
        return jload(paths.state)
    except Exception:
        return {}


def save_state(paths, state):
    os.makedirs(paths.runtime, exist_ok=True)
    with open(paths.state, 'w', encoding='utf-8') as handle:
        json.dump(state, handle)


# Glass panel. The blur is a crop of the wallpaper behind the panel.
# Rebuilt only when the size or the wallpaper file changes. Text is stamped on a copy.
GLASS_INSET = 36
PILL_W = 132
PILL_H = 32
OVERLAY_POLL_S = 30
_OVERLAY_MUTEX = 'Local\\DEGDesktopStatusOverlay'


def monitor_path(paths):
    return os.path.join(paths.runtime, 'monitor.txt')


def read_monitor(paths):
    try:
        text = open(monitor_path(paths), encoding='utf-8').read().strip().lower()
    except OSError:
        return 'on'
    return 'off' if text == 'off' else 'on'


def write_monitor(paths, mode):
    os.makedirs(paths.runtime, exist_ok=True)
    with open(monitor_path(paths), 'w', encoding='utf-8') as handle:
        handle.write(('off' if mode == 'off' else 'on') + '\n')


def overlay_size(screen, mode):
    if mode == 'off':
        return (PILL_W + GLASS_INSET * 2, PILL_H + GLASS_INSET * 2)
    width, height = screen
    margin = 44 if width >= 1600 else 16
    panel_w = 1330 if width >= 3000 else min(1330, max(420, width - margin * 2))
    panel_h = max(280, height - 36)
    return (panel_w, panel_h)


def window_origin(screen, size):
    width, _height = screen
    return max(0, width - size[0] - 16), 16


def pill_rect(size, mode):
    if mode == 'off':
        return (GLASS_INSET, GLASS_INSET, GLASS_INSET + PILL_W, GLASS_INSET + PILL_H)
    width, _height = size
    x1 = width - GLASS_INSET - 16
    y0 = GLASS_INSET + 14
    return (x1 - PILL_W, y0, x1, y0 + PILL_H)


def hit_test(x, y, size, mode):
    x0, y0, x1, y1 = pill_rect(size, mode)
    if x < x0 or y < y0 or x >= x1 or y >= y1:
        return None
    if x < (x0 + x1) / 2:
        return 'on'
    return 'off'


def panel_fingerprint(sections, now, mode):
    if mode != 'on':
        return 'off'
    clock = now.strftime('%Y-%m-%d %H:%M')
    head, _level = headline(sections)
    parts = ['on', clock, head]
    for _title, rows in sections:
        for item in rows:
            parts.append(item['label'] + '|' + item['text'] + '|' + item['level'])
    return '\n'.join(parts)


def crop_wallpaper(wallpaper, origin, size):
    width, height = size
    canvas = wallpaper.convert('RGB')
    if canvas.size[0] < origin[0] + width or canvas.size[1] < origin[1] + height:
        fitted = Image.new('RGB', (max(canvas.size[0], origin[0] + width), max(canvas.size[1], origin[1] + height)), (78, 90, 108))
        fitted.paste(canvas, (0, 0))
        canvas = fitted
    return canvas.crop((origin[0], origin[1], origin[0] + width, origin[1] + height))


def build_glass(size, wallpaper_crop):
    width, height = size
    image = Image.new('RGBA', size, (0, 0, 0, 0))
    x0, y0 = GLASS_INSET, GLASS_INSET
    x1, y1 = width - GLASS_INSET, height - GLASS_INSET
    if x1 <= x0 + 8 or y1 <= y0 + 8:
        return image
    shadow = Image.new('RGBA', size, (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((x0, y0 + 8, x1, y1 + 8), 28, fill=(0, 0, 0, 88))
    image.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(8)))
    fill = wallpaper_crop.convert('RGBA').resize((x1 - x0, y1 - y0), Image.LANCZOS)
    fill = fill.filter(ImageFilter.GaussianBlur(14))
    tint = Image.new('RGBA', fill.size, (14, 20, 32, 88))
    fill.alpha_composite(tint)
    sheen = Image.new('RGBA', fill.size, (0, 0, 0, 0))
    pen = ImageDraw.Draw(sheen)
    fill_h = max(1, fill.size[1] - 1)
    for y in range(fill.size[1]):
        fade = 1 - (y / fill_h)
        pen.line([(0, y), (fill.size[0], y)], fill=(255, 255, 255, int(72 * fade)))
    fill.alpha_composite(sheen)
    plate = Image.new('RGBA', size, (0, 0, 0, 0))
    plate.paste(fill, (x0, y0))
    mask = Image.new('L', size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((x0, y0, x1, y1), 28, fill=255)
    red, green, blue, alpha = plate.split()
    plate = Image.merge('RGBA', (red, green, blue, ImageChops.multiply(alpha, mask)))
    image.alpha_composite(plate)
    stroke = ImageDraw.Draw(image)
    stroke.rounded_rectangle((x0, y0, x1, y1), 28, outline=(255, 255, 255, 90), width=1)
    stroke.line([(x0 + 28, y0 + 2), (x1 - 28, y0 + 2)], fill=(255, 255, 255, 170), width=2)
    return image


def cached_glass(directory, key, build):
    os.makedirs(directory, exist_ok=True)
    key_path = os.path.join(directory, 'glass-key.txt')
    image_path = os.path.join(directory, 'glass-chrome.png')
    try:
        saved = open(key_path, encoding='utf-8').read()
    except OSError:
        saved = ''
    if saved == key and os.path.isfile(image_path):
        try:
            return Image.open(image_path).convert('RGBA'), False
        except Exception:
            pass
    image = build()
    image.save(image_path)
    with open(key_path, 'w', encoding='utf-8') as handle:
        handle.write(key)
    return image, True


def draw_pill(image, mode):
    x0, y0, x1, y1 = pill_rect(image.size, mode)
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((x0, y0, x1, y1), 16, fill=(255, 255, 255, 42), outline=(255, 255, 255, 110), width=1)
    mid = (x0 + x1) / 2
    if mode == 'off':
        chip = (mid + 1, y0 + 2, x1 - 2, y1 - 2)
    else:
        chip = (x0 + 2, y0 + 2, mid - 1, y1 - 2)
    draw.rounded_rectangle(chip, 14, fill=(255, 255, 255, 120))
    face = font('semibold', 13)
    draw.text((x0 + 18, y0 + 7), 'ON', font=face, fill=(255, 255, 255, 235))
    draw.text((mid + 14, y0 + 7), 'OFF', font=face, fill=(255, 255, 255, 235))


def stamp_overlay(image, sections, now, computer, mode):
    draw_pill(image, mode)
    if mode != 'on':
        return image
    draw = ImageDraw.Draw(image)
    x0 = GLASS_INSET + 28
    top = GLASS_INSET + 16
    right = image.width - GLASS_INSET - 20
    bottom = image.height - GLASS_INSET - 16
    head, level = headline(sections) if sections else ('All good', 'ok')
    dot(image, x0 + 14, top + 28, 11, COL[level])
    face_head = font('light', 34)
    draw.text((x0 + 36, top), fit(draw, head, face_head, right - x0 - PILL_W - 48), font=face_head, fill=(244, 247, 252, 255))
    stamp = now.strftime('%I:%M %p').lstrip('0')
    sub = f'{computer}  \u00b7  {stamp}' if computer else stamp
    draw.text((x0 + 36, top + 42), fit(draw, sub, font('body', 16), right - x0 - PILL_W - 48), font=font('body', 16), fill=(186, 198, 214, 255))
    nrows = sum(len(rows) for _, rows in sections)
    row_top = top + 78
    avail = bottom - row_top
    sec_h = 36 if avail > 320 else 22
    row_h = 28
    if nrows:
        row_h = min(32, (avail - sec_h * len(sections)) / nrows)
    if row_h < 12:
        row_h = 12
    font_size = int(min(18, max(10, row_h * 0.62)))
    face_label, face_text = font('semibold', font_size), font('body', font_size)
    face_sec = font('semibold', 13 if sec_h > 28 else 11)
    y = row_top
    for title, rows in sections:
        if y > bottom:
            break
        draw.text((x0, y), title.upper(), font=face_sec, fill=(176, 198, 224, 255))
        y += sec_h
        for item in rows:
            if y > bottom:
                break
            dot(image, x0 + 10, y + row_h / 2, 5, COL[item['level']])
            label_w = min(220, int((right - x0) * 0.34))
            draw.text((x0 + 24, y), fit(draw, item['label'], face_label, label_w), font=face_label, fill=(236, 240, 246, 255))
            dim = item['level'] == 'off'
            ink = (150, 162, 180, 255) if dim else (214, 222, 232, 255)
            draw.text((x0 + label_w + 16, y), fit(draw, item['text'], face_text, right - x0 - label_w - 24), font=face_text, fill=ink)
            y += row_h
    return image


def compose_overlay(sections, now, computer, mode, screen, wallpaper):
    size = overlay_size(screen, mode)
    origin = window_origin(screen, size)
    crop = crop_wallpaper(wallpaper, origin, size)
    chrome = build_glass(size, crop)
    return stamp_overlay(chrome.copy(), sections, now, computer, mode)


def wallpaper_surface(screen, path=''):
    if path and os.path.isfile(path):
        try:
            image = Image.open(path).convert('RGB').resize(screen, Image.LANCZOS)
            return image, str(os.path.getmtime(path))
        except Exception:
            pass
    return Image.new('RGB', screen, (78, 90, 108)), 'neutral'


def wallpaper_file():
    if winreg is None:
        return ''
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Control Panel\Desktop') as key:
            value, _kind = winreg.QueryValueEx(key, 'Wallpaper')
            return value or ''
    except OSError:
        return ''


def current_screen():
    if os.name == 'nt' and not os.environ.get('DEG_RENDER_SIZE'):
        ctypes.windll.user32.SetProcessDPIAware()
        user32 = ctypes.windll.user32
        return user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)
    return render_size()


def overlay_publish(paths, sections, now, computer, mode, screen, wallpaper, token, state):
    fingerprint = panel_fingerprint(sections, now, mode)
    dest = os.path.join(paths.render, 'overlay.png')
    if state.get('overlay_fp') == fingerprint and os.path.isfile(dest):
        return False
    os.makedirs(paths.render, exist_ok=True)
    size = overlay_size(screen, mode)
    origin = window_origin(screen, size)
    crop = crop_wallpaper(wallpaper, origin, size)
    key = f'{size[0]}x{size[1]}|{token}'
    chrome, _rebuilt = cached_glass(paths.runtime, key, lambda: build_glass(size, crop))
    image = stamp_overlay(chrome.copy(), sections, now, computer, mode)
    image.save(dest)
    state['overlay_fp'] = fingerprint
    save_state(paths, state)
    return True


def write_page(paths, sections, now, computer):
    page = render_html(sections, now, computer)
    with open(paths.html, 'w', encoding='utf-8-sig') as handle:
        handle.write(page)


def refresh_overlay(paths, host, state, screen=None):
    screen = screen or current_screen()
    mode = read_monitor(paths)
    path = '' if isinstance(host, FixtureHost) or os.name != 'nt' else wallpaper_file()
    wallpaper, token = wallpaper_surface(screen, path)
    if mode != 'on':
        now = dt.datetime.now().replace(second=0, microsecond=0)
        drew = overlay_publish(paths, [], now, '', 'off', screen, wallpaper, token, state)
        return drew, []
    snap = load_snapshot(host, paths, state)
    save_state(paths, state)
    sections = gather(snap)
    drew = overlay_publish(paths, sections, snap['now'], snap['computer_name'], 'on', screen, wallpaper, token, state)
    if drew:
        write_page(paths, sections, snap['now'], snap['computer_name'])
        if isinstance(host, FixtureHost):
            with open(os.path.join(paths.runtime, 'commands.json'), 'w', encoding='utf-8') as handle:
                json.dump(host.commands, handle)
    return drew, sections


def _utf8_stdio():
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, 'reconfigure', None)
        if reconfigure is None:
            continue
        try:
            reconfigure(encoding='utf-8')
        except Exception:
            pass


def parse_args(argv):
    fixture = None
    no_set = False
    doctor_only = False
    overlay = False
    index = 0
    while index < len(argv):
        arg = argv[index]
        if arg == '--no-set':
            no_set = True
        elif arg == '--doctor':
            doctor_only = True
        elif arg == '--overlay':
            overlay = True
        elif arg == '--fixture':
            index += 1
            if index >= len(argv):
                raise SystemExit('Missing folder after --fixture')
            fixture = argv[index]
        elif arg.startswith('--fixture='):
            fixture = arg.split('=', 1)[1]
        else:
            raise SystemExit(f'Unknown argument: {arg}')
        index += 1
    return fixture, no_set, doctor_only, overlay


def doctor(paths, fixture):
    print(f'python {sys.version.split()[0]}')
    print(f'pillow {Image.__version__}')
    print(f'psutil {psutil.__version__}')
    print(f'script {paths.here}')
    print(f'runtime {paths.runtime}')
    print(f'fixture {fixture or "none"}')
    print('wallpaper-set skipped-in-doctor')
    print('DOCTOR OK')


def run_board(argv):
    fixture, no_set, doctor_only, overlay = parse_args(argv)
    folder = None
    board = None
    if fixture:
        folder = fixture if os.path.isabs(fixture) else os.path.join(script_dir(), fixture)
        apply_fixture_env(folder)
        board = jload(os.path.join(folder, 'board.json'))
    paths = resolve_paths()
    if doctor_only:
        doctor(paths, folder)
        return 0
    if fixture:
        host = FixtureHost(folder, board)
    else:
        host = LiveHost()
    if overlay:
        return run_overlay(paths, host, fixture)
    os.makedirs(paths.render, exist_ok=True)
    state = load_state(paths)
    snap = load_snapshot(host, paths, state)
    sections = gather(snap)
    save_state(paths, state)
    image = render_png(sections, snap['now'], snap['computer_name'])
    flip = 'b' if state.get('flip') == 'a' else 'a'
    state['flip'] = flip
    save_state(paths, state)
    png = os.path.join(paths.render, f'wallpaper-{flip}.png')
    image.save(png)
    image.save(paths.latest)
    page = render_html(sections, snap['now'], snap['computer_name'])
    with open(paths.html, 'w', encoding='utf-8-sig') as handle:
        handle.write(page)
    if isinstance(host, FixtureHost):
        with open(os.path.join(paths.runtime, 'commands.json'), 'w', encoding='utf-8') as handle:
            json.dump(host.commands, handle)
    head, _level = headline(sections)
    print(f'Board: {head}')
    print(f'Wrote {paths.html}')
    print(f'Wrote {png}')
    if fixture:
        print('Wallpaper not changed (fixture)')
    elif no_set:
        print('Wallpaper not changed (--no-set)')
    elif os.name == 'nt':
        set_wallpaper(png)
        print(f'Wallpaper set {png}')
    else:
        print('Wallpaper not changed (not Windows)')
    return 0


def run_overlay(paths, host, fixture):
    os.makedirs(paths.runtime, exist_ok=True)
    os.makedirs(paths.render, exist_ok=True)
    if fixture or os.name != 'nt':
        state = load_state(paths)
        drew, sections = refresh_overlay(paths, host, state)
        if drew:
            head, _level = headline(sections) if sections else ('Off', 'info')
            print(f'Board: {head}')
            print('Overlay updated')
        else:
            print('Board unchanged')
        if fixture:
            print('Overlay window skipped (fixture)')
        else:
            print('Overlay window skipped (not Windows)')
        return 0
    if not _take_overlay_mutex():
        print('Overlay already running')
        return 0
    return _overlay_window_loop(paths, host)


def _take_overlay_mutex():
    kernel32 = ctypes.windll.kernel32
    kernel32.CreateMutexW.restype = ctypes.c_void_p
    handle = kernel32.CreateMutexW(None, False, _OVERLAY_MUTEX)
    if not handle:
        return False
    if kernel32.GetLastError() == 183:
        kernel32.CloseHandle(handle)
        return False
    return True


def _overlay_window_loop(paths, host):
    user32 = ctypes.windll.user32
    kernel32 = ctypes.windll.kernel32
    user32.DefWindowProcW.restype = ctypes.c_ssize_t
    user32.DefWindowProcW.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_size_t, ctypes.c_ssize_t]
    kernel32.GetModuleHandleW.restype = ctypes.c_void_p
    instance = kernel32.GetModuleHandleW(None)
    session = _OverlaySession(paths, host)

    def proc(hwnd, msg, wparam, lparam):
        if msg == 0x0084:
            point = _WinPoint()
            point.x = ctypes.c_short(lparam & 0xFFFF).value
            point.y = ctypes.c_short((lparam >> 16) & 0xFFFF).value
            user32.ScreenToClient(hwnd, ctypes.byref(point))
            if hit_test(point.x, point.y, session.size, session.mode):
                return 1
            return -1
        if msg == 0x0202:
            session.on_click(ctypes.c_short(lparam & 0xFFFF).value, ctypes.c_short((lparam >> 16) & 0xFFFF).value)
            return 0
        if msg == 0x0002:
            user32.PostQuitMessage(0)
            return 0
        return user32.DefWindowProcW(hwnd, msg, wparam, lparam)

    wndproc = _WNDPROC(proc)
    session.proc = wndproc
    window_class = _WNDCLASSW()
    window_class.lpfnWndProc = wndproc
    window_class.hInstance = instance
    window_class.lpszClassName = 'DEGDesktopStatusOverlay'
    user32.RegisterClassW(ctypes.byref(window_class))
    user32.CreateWindowExW.argtypes = [
        ctypes.c_uint, ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_uint,
        ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
        ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
    ]
    user32.CreateWindowExW.restype = ctypes.c_void_p
    hwnd = user32.CreateWindowExW(
        0x00080000 | 0x00000080 | 0x08000000,
        'DEGDesktopStatusOverlay',
        'Desktop status',
        0x80000000,
        0, 0, 10, 10,
        None, None, instance, None,
    )
    if not hwnd:
        raise RuntimeError('Could not create the overlay window')
    session.hwnd = hwnd
    user32.ShowWindow(hwnd, 4)
    session.tick(force=True)
    message = _WinMsg()
    while True:
        while user32.PeekMessageW(ctypes.byref(message), None, 0, 0, 1):
            if message.message == 0x0012:
                return 0
            user32.TranslateMessage(ctypes.byref(message))
            user32.DispatchMessageW(ctypes.byref(message))
        session.tick()
        kernel32.Sleep(200)


class _OverlaySession:
    def __init__(self, paths, host):
        self.paths = paths
        self.host = host
        self.state = load_state(paths)
        self.hwnd = None
        self.size = (10, 10)
        self.mode = 'on'
        self.next_tick = 0
        self.proc = None

    def tick(self, force=False):
        if not force and time.time() < self.next_tick:
            return
        self.next_tick = time.time() + OVERLAY_POLL_S
        self.mode = read_monitor(self.paths)
        if force:
            self.state.pop('overlay_fp', None)
        drew, _sections = refresh_overlay(self.paths, self.host, self.state)
        self.mode = read_monitor(self.paths)
        if not drew:
            print('Board unchanged')
            return
        image = Image.open(os.path.join(self.paths.render, 'overlay.png')).convert('RGBA')
        self.size = image.size
        origin = window_origin(current_screen(), self.size)
        _push_layered(self.hwnd, image, origin)
        print('Overlay updated')

    def on_click(self, x, y):
        hit = hit_test(x, y, self.size, self.mode)
        if hit is None or hit == self.mode:
            return
        write_monitor(self.paths, hit)
        self.state.pop('overlay_fp', None)
        self.tick(force=True)


class _WinPoint(ctypes.Structure):
    _fields_ = [('x', ctypes.c_long), ('y', ctypes.c_long)]


class _WinSize(ctypes.Structure):
    _fields_ = [('cx', ctypes.c_long), ('cy', ctypes.c_long)]


class _WinMsg(ctypes.Structure):
    _fields_ = [
        ('hwnd', ctypes.c_void_p),
        ('message', ctypes.c_uint),
        ('wParam', ctypes.c_size_t),
        ('lParam', ctypes.c_ssize_t),
        ('time', ctypes.c_uint),
        ('pt', _WinPoint),
    ]


class _BlendFunction(ctypes.Structure):
    _fields_ = [
        ('BlendOp', ctypes.c_ubyte),
        ('BlendFlags', ctypes.c_ubyte),
        ('SourceConstantAlpha', ctypes.c_ubyte),
        ('AlphaFormat', ctypes.c_ubyte),
    ]


class _BitmapInfoHeader(ctypes.Structure):
    _fields_ = [
        ('biSize', ctypes.c_uint32),
        ('biWidth', ctypes.c_long),
        ('biHeight', ctypes.c_long),
        ('biPlanes', ctypes.c_ushort),
        ('biBitCount', ctypes.c_ushort),
        ('biCompression', ctypes.c_uint32),
        ('biSizeImage', ctypes.c_uint32),
        ('biXPelsPerMeter', ctypes.c_long),
        ('biYPelsPerMeter', ctypes.c_long),
        ('biClrUsed', ctypes.c_uint32),
        ('biClrImportant', ctypes.c_uint32),
    ]


class _BitmapInfo(ctypes.Structure):
    _fields_ = [('bmiHeader', _BitmapInfoHeader), ('bmiColors', ctypes.c_uint32 * 3)]


_WNDPROC = getattr(ctypes, 'WINFUNCTYPE', ctypes.CFUNCTYPE)(
    ctypes.c_ssize_t, ctypes.c_void_p, ctypes.c_uint, ctypes.c_size_t, ctypes.c_ssize_t,
)


class _WNDCLASSW(ctypes.Structure):
    _fields_ = [
        ('style', ctypes.c_uint),
        ('lpfnWndProc', _WNDPROC),
        ('cbClsExtra', ctypes.c_int),
        ('cbWndExtra', ctypes.c_int),
        ('hInstance', ctypes.c_void_p),
        ('hIcon', ctypes.c_void_p),
        ('hCursor', ctypes.c_void_p),
        ('hbrBackground', ctypes.c_void_p),
        ('lpszMenuName', ctypes.c_wchar_p),
        ('lpszClassName', ctypes.c_wchar_p),
    ]


def _premultiply(image):
    red, green, blue, alpha = image.convert('RGBA').split()
    return Image.merge('RGBA', (
        ImageChops.multiply(red, alpha),
        ImageChops.multiply(green, alpha),
        ImageChops.multiply(blue, alpha),
        alpha,
    ))


def _push_layered(hwnd, image, origin):
    image = _premultiply(image)
    width, height = image.size
    user32 = ctypes.windll.user32
    gdi32 = ctypes.windll.gdi32
    user32.GetDC.restype = ctypes.c_void_p
    gdi32.CreateCompatibleDC.restype = ctypes.c_void_p
    gdi32.CreateDIBSection.restype = ctypes.c_void_p
    gdi32.SelectObject.restype = ctypes.c_void_p
    screen_dc = user32.GetDC(None)
    mem_dc = gdi32.CreateCompatibleDC(screen_dc)
    info = _BitmapInfo()
    info.bmiHeader.biSize = ctypes.sizeof(_BitmapInfoHeader)
    info.bmiHeader.biWidth = width
    info.bmiHeader.biHeight = -height
    info.bmiHeader.biPlanes = 1
    info.bmiHeader.biBitCount = 32
    info.bmiHeader.biCompression = 0
    bits = ctypes.c_void_p()
    dib = gdi32.CreateDIBSection(mem_dc, ctypes.byref(info), 0, ctypes.byref(bits), None, 0)
    if not dib or not bits:
        raise RuntimeError('Could not create the overlay bitmap')
    gdi32.SelectObject(mem_dc, dib)
    raw = image.tobytes('raw', 'BGRA')
    ctypes.memmove(bits, raw, len(raw))
    blend = _BlendFunction(0, 0, 255, 1)
    dst = _WinPoint(int(origin[0]), int(origin[1]))
    src = _WinPoint(0, 0)
    size = _WinSize(width, height)
    user32.UpdateLayeredWindow.argtypes = [
        ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(_WinPoint), ctypes.POINTER(_WinSize),
        ctypes.c_void_p, ctypes.POINTER(_WinPoint), ctypes.c_uint, ctypes.POINTER(_BlendFunction), ctypes.c_uint,
    ]
    user32.UpdateLayeredWindow.restype = ctypes.c_int
    ok = user32.UpdateLayeredWindow(hwnd, screen_dc, ctypes.byref(dst), ctypes.byref(size), mem_dc, ctypes.byref(src), 0, ctypes.byref(blend), 2)
    gdi32.DeleteObject(dib)
    gdi32.DeleteDC(mem_dc)
    user32.ReleaseDC(None, screen_dc)
    user32.SetWindowPos(hwnd, 1, 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010)
    if not ok:
        raise RuntimeError('UpdateLayeredWindow failed')


def main(argv=None):
    _utf8_stdio()
    args = list(sys.argv[1:] if argv is None else argv)
    try:
        return run_board(args)
    except SystemExit:
        raise
    except Exception:
        err_dir = os.environ.get('DEG_RUNTIME_DIR') or os.path.join(script_dir(), 'runtime')
        os.makedirs(err_dir, exist_ok=True)
        with open(os.path.join(err_dir, 'last-error.txt'), 'w', encoding='utf-8') as handle:
            handle.write(traceback.format_exc())
        raise


if __name__ == '__main__':
    sys.exit(main() or 0)
