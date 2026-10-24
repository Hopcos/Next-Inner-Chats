<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from 'vue'
import * as THREE from 'three'
import { kernel } from '@/kernel'

const canvasRef = ref<HTMLDivElement | null>(null)

let renderer: THREE.WebGLRenderer | null = null
let scene: THREE.Scene | null = null
let camera: THREE.PerspectiveCamera | null = null
let rafId = 0
/** 每帧更新回调（按场景注册） */
let tick: ((t: number, dt: number) => void) | null = null
/** 场景内需要释放的资源 */
const disposables: Array<{ dispose: () => void }> = []

const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false

const themeColor = () => {
  const theme = kernel.theme.state.theme
  if (theme === 'dawn') return new THREE.Color(0x4f46e5)
  if (theme === 'midnight') return new THREE.Color(0xa78bfa)
  return new THREE.Color(0x2dd4bf)
}

function track<T extends { dispose: () => void }>(o: T): T {
  disposables.push(o)
  return o
}

/** 默认场景：粒子星云 + 旋转线框二十面体 */
function buildNebula(s: THREE.Scene) {
  const count = 2600
  const positions = new Float32Array(count * 3)
  for (let i = 0; i < count * 3; i += 3) {
    positions[i] = (Math.random() - 0.5) * 700
    positions[i + 1] = (Math.random() - 0.5) * 500
    positions[i + 2] = (Math.random() - 0.5) * 700
  }
  const geo = track(new THREE.BufferGeometry())
  geo.setAttribute('position', new THREE.BufferAttribute(positions, 3))
  const mat = track(
    new THREE.PointsMaterial({
      color: themeColor(),
      size: 1.4,
      transparent: true,
      opacity: 0.75,
      sizeAttenuation: true,
      blending: THREE.AdditiveBlending,
    }),
  )
  const points = new THREE.Points(geo, mat)
  s.add(points)

  const ico = track(new THREE.IcosahedronGeometry(60, 1))
  const wireGeo = track(new THREE.WireframeGeometry(ico))
  const wireMat = track(new THREE.LineBasicMaterial({ color: themeColor(), transparent: true, opacity: 0.22 }))
  const wire = new THREE.LineSegments(wireGeo, wireMat)
  s.add(wire)

  camera!.position.set(0, 0, 220)
  tick = (t) => {
    points.rotation.y = t * 0.02
    points.rotation.x = Math.sin(t * 0.01) * 0.1
    wire.rotation.y = t * 0.1
    wire.rotation.x = t * 0.06
  }
}

/** 渐变条纹落日（Canvas 纹理：上黄下洋红 + 底部横向镂空条纹，经典合成波太阳） */
function sunTexture(): THREE.CanvasTexture {
  const c = document.createElement('canvas')
  c.width = c.height = 512
  const g = c.getContext('2d')!
  const grad = g.createLinearGradient(0, 0, 0, 512)
  grad.addColorStop(0, '#ffe66d')
  grad.addColorStop(0.45, '#ff7a3d')
  grad.addColorStop(1, '#ff2bd6')
  g.fillStyle = grad
  g.beginPath()
  g.arc(256, 256, 250, 0, Math.PI * 2)
  g.fill()
  g.globalCompositeOperation = 'destination-out'
  for (let i = 0; i < 9; i++) {
    const y = 290 + i * 26
    const h = 3 + i * 1.6
    g.fillRect(0, y, 512, h)
  }
  const tex = new THREE.CanvasTexture(c)
  tex.colorSpace = THREE.SRGBColorSpace
  return tex
}

/** 赛博朋克场景：无限前移的霓虹网格地面 + 两侧线框摩天楼 + 条纹落日 + 数字雨 */
function buildCyber(s: THREE.Scene) {
  const CYAN = new THREE.Color(0x00f0ff)
  const MAGENTA = new THREE.Color(0xff2bd6)
  const VIOLET = new THREE.Color(0x7c3aed)

  s.fog = new THREE.Fog(0x07040f, 260, 1500)
  camera!.far = 3000
  camera!.updateProjectionMatrix()
  camera!.position.set(0, 26, 120)
  camera!.lookAt(0, 60, -600)

  // 1) 网格地面：单张大网格按“一个格距”取模前移 → 视觉上无限、无缝、相机脚下永不出现空洞
  const GRID = 2400
  const CELL = 20
  const grid = new THREE.GridHelper(GRID, GRID / CELL, MAGENTA, MAGENTA)
  const gridMat = grid.material as THREE.Material
  gridMat.transparent = true
  gridMat.opacity = 0.55
  track(grid.geometry)
  track(gridMat)
  const GRID_Z = -GRID / 2 + 300 // 覆盖相机后方 300 到前方 2100
  grid.position.set(0, 0, GRID_Z)
  s.add(grid)
  let travel = 0

  // 2) 落日
  const sunTex = track(sunTexture())
  const sunMat = track(new THREE.MeshBasicMaterial({ map: sunTex, transparent: true, fog: false, depthWrite: false }))
  const sunGeo = track(new THREE.PlaneGeometry(760, 760))
  const sun = new THREE.Mesh(sunGeo, sunMat)
  sun.renderOrder = -1
  sun.position.set(0, 300, -1900)
  s.add(sun)

  // 3) 两侧线框高楼（Edges 只画轮廓，青/紫交替，顶端洋红航标灯）
  const box = track(new THREE.BoxGeometry(1, 1, 1))
  const edges = track(new THREE.EdgesGeometry(box))
  const matCyan = track(new THREE.LineBasicMaterial({ color: CYAN, transparent: true, opacity: 0.75 }))
  const matViolet = track(new THREE.LineBasicMaterial({ color: VIOLET, transparent: true, opacity: 0.85 }))
  const fillMat = track(new THREE.MeshBasicMaterial({ color: 0x07040f, transparent: true, opacity: 0.88 }))
  const beaconGeo = track(new THREE.SphereGeometry(1.4, 8, 8))
  const beaconMat = track(new THREE.MeshBasicMaterial({ color: MAGENTA }))
  const city = new THREE.Group()
  const SPAN = 1400
  for (let i = 0; i < 70; i++) {
    const side = i % 2 === 0 ? -1 : 1
    const w = 18 + Math.random() * 30
    const d = 18 + Math.random() * 30
    const h = 40 + Math.pow(Math.random(), 1.8) * 260
    const x = side * (90 + Math.random() * 260)
    const z = -Math.random() * SPAN
    const b = new THREE.Group()
    const fill = new THREE.Mesh(box, fillMat)
    const line = new THREE.LineSegments(edges, Math.random() < 0.6 ? matCyan : matViolet)
    fill.scale.set(w, h, d)
    line.scale.set(w, h, d)
    b.add(fill, line)
    if (Math.random() < 0.45) {
      const beacon = new THREE.Mesh(beaconGeo, beaconMat)
      beacon.position.y = h / 2 + 2
      b.add(beacon)
    }
    b.position.set(x, h / 2, z)
    city.add(b)
  }
  s.add(city)

  // 4) 数字雨：青色粒子竖直下落
  const RAIN = 900
  const rainPos = new Float32Array(RAIN * 3)
  const rainSpeed = new Float32Array(RAIN)
  for (let i = 0; i < RAIN; i++) {
    rainPos[i * 3] = (Math.random() - 0.5) * 900
    rainPos[i * 3 + 1] = Math.random() * 420
    rainPos[i * 3 + 2] = -Math.random() * 900
    rainSpeed[i] = 40 + Math.random() * 90
  }
  const rainGeo = track(new THREE.BufferGeometry())
  rainGeo.setAttribute('position', new THREE.BufferAttribute(rainPos, 3))
  const rainMat = track(
    new THREE.PointsMaterial({ color: CYAN, size: 1.6, transparent: true, opacity: 0.7, blending: THREE.AdditiveBlending, depthWrite: false }),
  )
  s.add(new THREE.Points(rainGeo, rainMat))

  const SPEED = 46
  tick = (t, dt) => {
    // 地面网格前移：位移对格距取模（网格线周期性 → 回卷不可见）
    travel = (travel + SPEED * dt) % CELL
    grid.position.z = GRID_Z + travel
    // 城市同速前移，越过相机后回到远端
    for (const b of city.children) {
      b.position.z += SPEED * dt
      if (b.position.z > 140) b.position.z -= SPAN
    }
    const attr = rainGeo.getAttribute('position') as THREE.BufferAttribute
    for (let i = 0; i < RAIN; i++) {
      let y = attr.getY(i) - rainSpeed[i] * dt
      if (y < 0) y += 420
      attr.setY(i, y)
    }
    attr.needsUpdate = true
    // 轻微镜头漂移 + 落日呼吸
    camera!.position.x = Math.sin(t * 0.15) * 10
    camera!.lookAt(Math.sin(t * 0.15) * 4, 60, -600)
    sunMat.opacity = 0.88 + Math.sin(t * 1.3) * 0.08
  }
}

function build() {
  const host = canvasRef.value
  if (!host || renderer || !kernel.settings.state.threeEnabled) return

  scene = new THREE.Scene()
  camera = new THREE.PerspectiveCamera(70, host.clientWidth / host.clientHeight, 0.1, 2000)

  renderer = new THREE.WebGLRenderer({ alpha: true, antialias: true })
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2))
  renderer.setSize(host.clientWidth, host.clientHeight)
  host.appendChild(renderer.domElement)

  const cyber = kernel.theme.state.theme === 'cyber'
  if (cyber) buildCyber(scene)
  else buildNebula(scene)
  document.documentElement.classList.add('nc-3d')

  let last = performance.now()
  const animate = () => {
    rafId = requestAnimationFrame(animate)
    const now = performance.now()
    // 后台标签页恢复时 dt 可能极大，钳制防跳帧
    const dt = Math.min((now - last) / 1000, 0.1)
    last = now
    if (!reduceMotion) tick?.(now / 1000, dt)
    renderer?.render(scene!, camera!)
  }
  animate()
}

function onResize() {
  const host = canvasRef.value
  if (!host || !renderer || !camera) return
  camera.aspect = host.clientWidth / host.clientHeight
  camera.updateProjectionMatrix()
  renderer.setSize(host.clientWidth, host.clientHeight)
}

function dispose() {
  cancelAnimationFrame(rafId)
  tick = null
  for (const d of disposables.splice(0)) d.dispose()
  renderer?.dispose()
  renderer?.domElement.remove()
  renderer = null
  scene = null
  camera = null
  document.documentElement.classList.remove('nc-3d')
}

onMounted(() => {
  build()
  window.addEventListener('resize', onResize)
})
onUnmounted(() => {
  window.removeEventListener('resize', onResize)
  dispose()
})

// 主题切换：销毁后按新主题重建（此前 build() 遇到已有 renderer 直接返回，换肤不生效）
watch(
  () => kernel.theme.state.theme,
  () => {
    if (!renderer) return
    dispose()
    build()
  },
)

// 3D 开关变化：重建（启用）或销毁（禁用）
watch(
  () => kernel.settings.state.threeEnabled,
  (enabled) => {
    if (enabled && !renderer) build()
    if (!enabled) dispose()
  },
)
</script>

<template>
  <div ref="canvasRef" class="three-stage" aria-hidden="true" />
</template>
