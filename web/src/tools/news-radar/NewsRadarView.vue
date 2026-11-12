<script setup lang="ts">
/**
 * News Radar：双语 RSS 新闻雷达与混合检索（Dense + BM25 / RRF）。
 * 遵循 Kafka Producer 等工具的设计与编码规范：
 * - 顶部查询条件：新闻源（下拉）、关键字（回车或按钮查询）、模式（hybrid / dense / sparse，仅对关键字生效）、数量（5 / 10 / 20 / 50，默认 10）、查询按钮
 * - 下方新闻列表：页面滚动、卡片式短新闻流，区分明显、排版美观引人注目，与当前主题无缝契合
 * - 保证“打开原文”始终保持在第一行最右侧不折行，作者设置最大宽度截断，超长支持 Tooltip 悬浮查看
 * - 标题与正文之间具备鲜明层次感：标题加深加粗带呼吸下垫面与精致左引条，正文以柔和色温与优良行距展现
 * - 双语展示：默认展示 title_zh 与 description_zh；当 language !== 'zh' 时支持一键切换查看原文 / 译文
 * - 标题纯文本展示不跳转；外链在卡片右上角“打开原文”在新标签打开
 * - 过滤正文末尾附带的多余 Link: url 文本
 */
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { http } from '@/api/http'
import { kernel } from '@/kernel'
import type { NewsItem, NewsListResponse, NewsSearchResponse, NewsSource, SearchMode, SourcesResponse } from './types'

const { t } = useI18n()
const TOOL_KEY = 'news-radar'

// ---------------- 端点配置校验 ----------------
const cfgOk = ref(false)
const endpoint = '/api/ext/tool/news-radar'

async function loadBaseUrl() {
  try {
    const list = await http.get<{ key: string; baseUrl?: string | null }[]>('/api/me/tools')
    const me = list.find((x) => x.key === TOOL_KEY)
    if (me?.baseUrl) cfgOk.value = true
  } catch {
    /* 忽略：按未配置展示提示 */
  }
}

// ---------------- 查询条件状态 ----------------
const sources = ref<NewsSource[]>([])
const selectedSource = ref('')
const keyword = ref('')
const searchMode = ref<SearchMode>('hybrid')
const limit = ref<number>(10)
const loadingSources = ref(false)
const loadingNews = ref(false)

const items = ref<NewsItem[]>([])
const totalCount = ref<number>(0)
const lastQuery = ref<string>('')
const lastMode = ref<string>('')

// 记录哪些新闻当前切换到了"原文模式"（key 为新闻 id）
const showOriginalMap = ref<Record<string, boolean>>({})

function toggleLanguage(id: string) {
  showOriginalMap.value[id] = !showOriginalMap.value[id]
}

function isOriginal(id: string): boolean {
  return !!showOriginalMap.value[id]
}

/** 清理正文末尾类似 "Link: https://..." 或 "https://..." 冗余链接 */
function cleanDescription(text: string | null | undefined): string {
  if (!text) return ''
  let cleaned = text.trim()
  // 匹配末尾的 Link: https?://... 或换行后的 https?://...
  cleaned = cleaned.replace(/(?:\r?\n|\s)+(?:Link:\s*)?https?:\/\/[^\s]+$/i, '').trim()
  return cleaned
}

// ---------------- 数据请求 ----------------
async function loadSources() {
  if (!cfgOk.value) return
  loadingSources.value = true
  try {
    const res = await http.get<SourcesResponse>(`${endpoint}/api/v1/sources`)
    sources.value = res?.sources ?? []
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('tools.newsRadar.loadSourcesFailed'), (e as { code?: string }).code)
  } finally {
    loadingSources.value = false
  }
}

async function fetchNews() {
  if (!cfgOk.value) return
  loadingNews.value = true
  showOriginalMap.value = {}
  try {
    const trimmed = keyword.value.trim()
    if (trimmed) {
      // 关键字搜索模式
      const params = new URLSearchParams()
      params.set('q', trimmed)
      params.set('limit', String(limit.value))
      params.set('mode', searchMode.value)
      if (selectedSource.value) {
        params.set('source', selectedSource.value)
      }
      const res = await http.get<NewsSearchResponse>(`${endpoint}/api/v1/news/search?${params.toString()}`)
      items.value = res?.items ?? []
      totalCount.value = res?.count ?? items.value.length
      lastQuery.value = res?.query ?? trimmed
      lastMode.value = res?.mode ?? searchMode.value
    } else {
      // 默认最新列表模式
      const params = new URLSearchParams()
      params.set('limit', String(limit.value))
      if (selectedSource.value) {
        params.set('source', selectedSource.value)
      }
      const res = await http.get<NewsListResponse>(`${endpoint}/api/v1/news?${params.toString()}`)
      items.value = res?.items ?? []
      totalCount.value = res?.count ?? items.value.length
      lastQuery.value = ''
      lastMode.value = ''
    }
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('tools.newsRadar.queryFailed'), (e as { code?: string }).code)
    items.value = []
    totalCount.value = 0
  } finally {
    loadingNews.value = false
  }
}

// 格式化时间：转为易读的本地时间
function formatTime(iso: string): string {
  if (!iso) return ''
  try {
    const d = new Date(iso)
    if (Number.isNaN(d.getTime())) return iso
    const pad = (n: number) => String(n).padStart(2, '0')
    const y = d.getFullYear()
    const m = pad(d.getMonth() + 1)
    const day = pad(d.getDate())
    const hh = pad(d.getHours())
    const mm = pad(d.getMinutes())
    const ss = pad(d.getSeconds())
    return `${y}-${m}-${day} ${hh}:${mm}:${ss}`
  } catch {
    return iso
  }
}

onMounted(async () => {
  await loadBaseUrl()
  if (cfgOk.value) {
    await loadSources()
    await fetchNews()
  }
})
</script>

<template>
  <div class="nr-page">
    <div class="nr-scroll-body">
      <!-- 顶部：查询条件面板 -->
      <div class="nr-card nr-form">
        <div class="nr-row">
          <!-- 新闻源 -->
          <label class="nr-field">
            <span class="nr-label">{{ t('tools.newsRadar.source') }}</span>
            <el-select
              v-model="selectedSource"
              size="small"
              class="nr-ctrl"
              clearable
              :placeholder="t('tools.newsRadar.allSources')"
              :loading="loadingSources"
            >
              <el-option :label="t('tools.newsRadar.allSources')" value="" />
              <el-option
                v-for="s in sources"
                :key="s.name"
                :label="s.name"
                :value="s.name"
              />
            </el-select>
          </label>

          <!-- 关键字 -->
          <label class="nr-field nr-field-kw">
            <span class="nr-label">{{ t('tools.newsRadar.keyword') }}</span>
            <el-input
              v-model="keyword"
              size="small"
              class="nr-ctrl nr-kw-input"
              clearable
              :placeholder="t('tools.newsRadar.keywordPh')"
              @keydown.enter="fetchNews"
            />
          </label>

          <!-- 检索模式（仅对关键字查询有用） -->
          <label class="nr-field">
            <span class="nr-label">
              {{ t('tools.newsRadar.mode') }}
              <span v-if="!keyword.trim()" class="nr-label-hint">({{ t('tools.newsRadar.modeHintKeywordOnly') }})</span>
            </span>
            <el-select
              v-model="searchMode"
              size="small"
              class="nr-ctrl nr-mode-ctrl"
              :disabled="!keyword.trim()"
            >
              <el-option label="hybrid (混合检索)" value="hybrid" />
              <el-option label="dense (语义向量)" value="dense" />
              <el-option label="sparse (关键字 BM25)" value="sparse" />
            </el-select>
          </label>

          <!-- 数量下拉 -->
          <label class="nr-field nr-field-limit">
            <span class="nr-label">{{ t('tools.newsRadar.limit') }}</span>
            <el-select v-model="limit" size="small" class="nr-ctrl nr-limit-ctrl">
              <el-option :label="5" :value="5" />
              <el-option :label="10" :value="10" />
              <el-option :label="20" :value="20" />
              <el-option :label="50" :value="50" />
            </el-select>
          </label>

          <!-- 查询按钮 -->
          <div class="nr-actions">
            <el-button
              type="primary"
              size="small"
              class="nr-btn-query"
              :loading="loadingNews"
              @click="fetchNews"
            >
              <svg
                viewBox="0 0 24 24"
                width="14"
                height="14"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
                style="margin-right: 4px;"
              >
                <circle cx="11" cy="11" r="8" />
                <path d="m21 21-4.35-4.35" />
              </svg>
              {{ t('tools.newsRadar.query') }}
            </el-button>
          </div>
        </div>
      </div>

      <!-- 未配置端点提示 -->
      <div v-if="!cfgOk" class="nr-card nr-tip-card">
        <div class="nr-tip-title">{{ t('tools.newsRadar.notConfigured') }}</div>
        <div class="nr-tip-desc">{{ t('tools.newsRadar.notConfiguredDesc') }}</div>
      </div>

      <!-- 主体：短新闻列表展示区 -->
      <div v-else class="nr-news-container">
        <!-- 状态摘要条 -->
        <div class="nr-status-bar">
          <span class="nr-status-count">
            {{ t('tools.newsRadar.totalFound', { count: totalCount }) }}
          </span>
          <span v-if="lastQuery" class="nr-status-badge">
            {{ t('tools.newsRadar.badgeQuery') }}: "{{ lastQuery }}" ({{ lastMode }})
          </span>
          <span v-else class="nr-status-badge">
            {{ t('tools.newsRadar.badgeLatest') }}
          </span>
        </div>

        <!-- 加载中 -->
        <div v-if="loadingNews" class="nr-card nr-empty">
          <span class="nr-loading-spinner" />
          <span class="nr-empty-text">{{ t('tools.newsRadar.loading') }}</span>
        </div>

        <!-- 空结果 -->
        <div v-else-if="items.length === 0" class="nr-card nr-empty">
          <svg
            viewBox="0 0 24 24"
            width="40"
            height="40"
            fill="none"
            stroke="currentColor"
            stroke-width="1.5"
            stroke-linecap="round"
            stroke-linejoin="round"
            class="nr-empty-icon"
          >
            <path d="M4 22h16a2 2 0 0 0 2-2V4a2 2 0 0 0-2-2H8a2 2 0 0 0-2 2v16a2 2 0 0 1-2 2Zm0 0a2 2 0 0 1-2-2v-9c0-1.1.9-2 2-2h2" />
            <path d="M18 14h-8" />
            <path d="M15 18h-5" />
            <path d="M10 6h8v4h-8V6Z" />
          </svg>
          <span class="nr-empty-text">{{ t('tools.newsRadar.empty') }}</span>
        </div>

        <!-- 新闻流列表 -->
        <div v-else class="nr-feed">
          <article
            v-for="item in items"
            :key="item.id"
            class="nr-card nr-news-item"
          >
            <!-- 左侧强调色带装饰 -->
            <div class="nr-accent-bar" />

            <!-- 新闻内容主体包装 -->
            <div class="nr-item-content">
              <!-- 第一行：顶部元数据与操作行（无论屏幕宽度，严禁换行折叠，保证“打开原文”稳稳保持在第一行最右侧） -->
              <header class="nr-item-head">
                <div class="nr-meta-left">
                  <!-- 新闻源标识 -->
                  <span class="nr-badge-source">
                    <span class="nr-source-dot" />
                    {{ item.source }}
                  </span>

                  <!-- 作者（限制最大宽度，超长截断，支持悬浮完整提示） -->
                  <el-tooltip
                    v-if="item.author"
                    :content="item.author"
                    placement="top"
                    :show-after="200"
                  >
                    <span class="nr-author">
                      <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" class="nr-author-ico">
                        <path d="M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2" />
                        <circle cx="12" cy="7" r="4" />
                      </svg>
                      <span class="nr-author-text">{{ item.author }}</span>
                    </span>
                  </el-tooltip>

                  <!-- 发布时间 -->
                  <span class="nr-time" :title="item.published_at">
                    <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                      <circle cx="12" cy="12" r="10" />
                      <polyline points="12 6 12 12 16 14" />
                    </svg>
                    <span>{{ formatTime(item.published_at) }}</span>
                  </span>

                  <!-- 分类 tags（空间不足时可自适应隐藏或裁剪） -->
                  <div v-if="item.categories && item.categories.length" class="nr-tags">
                    <span v-for="cat in item.categories.slice(0, 2)" :key="cat" class="nr-tag">
                      {{ cat }}
                    </span>
                  </div>
                </div>

                <!-- 第一行右侧：语言切换与打开原文外链 -->
                <div class="nr-meta-right">
                  <!-- 语言切换按钮：当 language !== 'zh' 时展示 -->
                  <button
                    v-if="item.language !== 'zh'"
                    type="button"
                    class="nr-btn-lang"
                    :class="{ active: isOriginal(item.id) }"
                    :title="isOriginal(item.id) ? t('tools.newsRadar.switchToZh') : t('tools.newsRadar.switchToOriginal')"
                    @click="toggleLanguage(item.id)"
                  >
                    <!-- 翻译/语言 ICON -->
                    <svg
                      viewBox="0 0 24 24"
                      width="13"
                      height="13"
                      fill="none"
                      stroke="currentColor"
                      stroke-width="1.8"
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      aria-hidden="true"
                    >
                      <path d="m5 8 6 6" />
                      <path d="m4 14 6-6 2-3" />
                      <path d="M2 5h12" />
                      <path d="M7 2h1" />
                      <path d="m22 22-5-10-5 10" />
                      <path d="M14 18h6" />
                    </svg>
                    <span class="nr-lang-text">
                      {{ isOriginal(item.id) ? t('tools.newsRadar.btnShowZh') : t('tools.newsRadar.btnShowOriginal') }}
                    </span>
                  </button>

                  <!-- 打开原文链接按钮（牢牢固定在第一行右侧） -->
                  <a
                    :href="item.link"
                    target="_blank"
                    rel="noopener noreferrer"
                    class="nr-link-btn"
                    :title="t('tools.newsRadar.openInNewTab')"
                  >
                    <span>{{ t('tools.newsRadar.openLink') }}</span>
                    <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                      <path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" />
                      <polyline points="15 3 21 3 21 9" />
                      <line x1="10" y1="14" x2="21" y2="3" />
                    </svg>
                  </a>
                </div>
              </header>

              <!-- 文章标题区域：加深、加粗、独立背景衬托，强化与正文之间的层次感 -->
              <div class="nr-title-block">
                <span class="nr-title-indicator" />
                <h2 class="nr-title">
                  {{ isOriginal(item.id) ? (item.title_original || item.title_zh) : (item.title_zh || item.title_original) }}
                </h2>
              </div>

              <!-- 新闻描述正文区域：柔和字色、适宜行高与轻微内缩，形成自然阅读层次 -->
              <div class="nr-desc-block">
                <div class="nr-desc">
                  {{ cleanDescription(isOriginal(item.id) ? (item.description_original || item.description_zh) : (item.description_zh || item.description_original)) }}
                </div>
              </div>
            </div>
          </article>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* 整个页面视口高度固定，内部具备独立平滑垂直滚动条 */
.nr-page {
  height: 100%;
  min-height: 0;
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  box-sizing: border-box;
}

.nr-scroll-body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  overflow-x: hidden;
  padding: 18px 24px 36px;
  display: flex;
  flex-direction: column;
  gap: 16px;
  max-width: 1240px;
  width: 100%;
  margin: 0 auto;
  box-sizing: border-box;
}

/* 统一卡片容器基类 */
.nr-card {
  background: var(--nc-surface);
  border: 1px solid var(--nc-border);
  border-radius: 12px;
  box-shadow: var(--nc-shadow-sm, 0 1px 3px rgba(0, 0, 0, 0.05));
  transition: border-color 0.2s ease, box-shadow 0.2s ease, transform 0.2s ease;
}

/* 顶部搜索条 */
.nr-form {
  padding: 16px 20px;
  background: var(--nc-surface);
  flex-shrink: 0;
}

.nr-row {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 12px 16px;
}

.nr-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.nr-label {
  font-size: 12px;
  font-weight: 600;
  color: var(--nc-text-muted);
  user-select: none;
}

.nr-label-hint {
  font-size: 11px;
  font-weight: 400;
  color: var(--nc-text-faint);
  margin-left: 2px;
}

.nr-ctrl {
  min-width: 140px;
}

.nr-field-kw {
  flex: 1 1 240px;
}

.nr-kw-input {
  width: 100%;
}

.nr-mode-ctrl {
  width: 180px;
}

.nr-field-limit {
  width: 80px;
}

.nr-limit-ctrl {
  width: 80px;
  min-width: 80px;
}

.nr-actions {
  display: flex;
  align-items: flex-end;
}

.nr-btn-query {
  border-radius: 8px;
  padding: 0 18px;
  font-weight: 600;
  height: 32px;
}

/* 提示卡片 */
.nr-tip-card {
  padding: 28px;
  text-align: center;
}

.nr-tip-title {
  font-size: 15px;
  font-weight: 600;
  color: var(--nc-text);
  margin-bottom: 6px;
}

.nr-tip-desc {
  font-size: 13px;
  color: var(--nc-text-muted);
}

.nr-news-container {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.nr-status-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 2px 4px;
  font-size: 12px;
  color: var(--nc-text-muted);
}

.nr-status-count {
  font-weight: 600;
}

.nr-status-badge {
  background: var(--nc-border);
  color: var(--nc-text-muted);
  padding: 3px 10px;
  border-radius: 6px;
  font-size: 11.5px;
}

.nr-empty {
  padding: 56px 24px;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  color: var(--nc-text-faint);
}

.nr-empty-icon {
  opacity: 0.5;
}

.nr-empty-text {
  font-size: 14px;
}

.nr-loading-spinner {
  width: 26px;
  height: 26px;
  border: 2.5px solid var(--nc-border);
  border-top-color: var(--nc-primary);
  border-radius: 50%;
  animation: nr-spin 0.8s linear infinite;
}

@keyframes nr-spin {
  to {
    transform: rotate(360deg);
  }
}

/* 新闻流 */
.nr-feed {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

/* 新闻单篇卡片：排版层次鲜明、呼吸感充足 */
.nr-news-item {
  position: relative;
  display: flex;
  padding: 0;
  overflow: hidden;
  background: var(--nc-surface);
  border: 1px solid var(--nc-border);
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.04);
}

.nr-news-item:hover {
  border-color: color-mix(in srgb, var(--nc-primary) 45%, var(--nc-border));
  box-shadow: 0 6px 20px color-mix(in srgb, var(--nc-primary) 12%, rgba(0, 0, 0, 0.08));
  transform: translateY(-1px);
}

/* 侧边引人注目的渐变条 */
.nr-accent-bar {
  width: 5px;
  flex-shrink: 0;
  background: linear-gradient(180deg, var(--nc-primary), color-mix(in srgb, var(--nc-primary) 50%, #8b5cf6));
  opacity: 0.85;
  transition: opacity 0.2s ease, width 0.2s ease;
}

.nr-news-item:hover .nr-accent-bar {
  opacity: 1;
  width: 6px;
}

/* 卡片内部正文布局 */
.nr-item-content {
  flex: 1;
  min-width: 0;
  padding: 16px 22px 20px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

/* 第一行：顶部元数据行（保证“打开原文”始终在第一行最右侧，使用 flex-nowrap + flex-shrink:0 杜绝折行） */
.nr-item-head {
  display: flex;
  flex-wrap: nowrap;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  font-size: 12px;
  border-bottom: 1px solid color-mix(in srgb, var(--nc-border) 60%, transparent);
  padding-bottom: 10px;
}

.nr-meta-left {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-wrap: nowrap;
  align-items: center;
  gap: 8px 12px;
  overflow: hidden;
}

/* 新闻源 Badge：立体精致小胶囊 */
.nr-badge-source {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-weight: 700;
  font-size: 11px;
  padding: 2.5px 8px;
  border-radius: 6px;
  background: color-mix(in srgb, var(--nc-primary) 12%, transparent);
  color: var(--nc-primary);
  letter-spacing: 0.4px;
  text-transform: uppercase;
}

.nr-source-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--nc-primary);
  display: inline-block;
}

/* 作者展示：限制最大宽度为 120px，超出显示省略号，鼠标移上可 Tooltip 浮动完整查看 */
.nr-author {
  flex-shrink: 1;
  min-width: 0;
  max-width: 140px;
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--nc-text-muted);
  font-size: 12px;
  cursor: default;
}

.nr-author-ico {
  flex-shrink: 0;
}

.nr-author-text {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 发布时间 */
.nr-time {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--nc-text-faint);
  font-size: 11.5px;
  white-space: nowrap;
}

.nr-tags {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.nr-tag {
  font-size: 11px;
  padding: 1.5px 6px;
  border-radius: 4px;
  background: var(--nc-bg);
  border: 1px solid var(--nc-border);
  color: var(--nc-text-faint);
  white-space: nowrap;
}

/* 第一行右侧：强力保留在首行，绝不被挤压 */
.nr-meta-right {
  flex-shrink: 0;
  display: flex;
  align-items: center;
  gap: 8px;
  white-space: nowrap;
}

/* 语言切换按钮 */
.nr-btn-lang {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 9px;
  border-radius: 6px;
  border: 1px solid var(--nc-border);
  background: var(--nc-bg);
  color: var(--nc-text-muted);
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
  transition: all 0.15s ease;
  user-select: none;
}

.nr-btn-lang:hover {
  color: var(--nc-primary);
  border-color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 10%, transparent);
}

.nr-btn-lang.active {
  color: var(--nc-primary);
  border-color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 15%, transparent);
  font-weight: 600;
}

/* 打开原文按钮：保持在第一行，高亮易点 */
.nr-link-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3.5px 11px;
  border-radius: 6px;
  border: 1px solid color-mix(in srgb, var(--nc-primary) 32%, var(--nc-border));
  background: color-mix(in srgb, var(--nc-primary) 8%, transparent);
  color: var(--nc-primary);
  text-decoration: none;
  font-size: 11.5px;
  font-weight: 600;
  transition: all 0.15s ease;
}

.nr-link-btn:hover {
  background: var(--nc-primary);
  color: #fff;
  border-color: var(--nc-primary);
  box-shadow: 0 2px 6px color-mix(in srgb, var(--nc-primary) 30%, transparent);
}

/* 标题区域：与正文形成鲜明的层次感设计 */
.nr-title-block {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 4px 0 2px;
}

.nr-title-indicator {
  width: 3.5px;
  height: 16px;
  border-radius: 2px;
  background: var(--nc-primary);
  margin-top: 5px;
  flex-shrink: 0;
  opacity: 0.9;
}

/* 新闻标题：大方醒目、高对比色阶（--nc-text），与正文区分 */
.nr-title {
  margin: 0;
  font-size: 17px;
  line-height: 1.48;
  font-weight: 700;
  color: var(--nc-text);
  letter-spacing: -0.25px;
  word-break: break-word;
}

/* 正文容器块：内缩 + 背景微晕与顶部分隔线，形成显著的层级景深 */
.nr-desc-block {
  background: color-mix(in srgb, var(--nc-bg) 65%, transparent);
  border: 1px solid color-mix(in srgb, var(--nc-border) 45%, transparent);
  border-radius: 8px;
  padding: 12px 16px;
  margin-top: 2px;
}

/* 描述全文：字号13.8px、行高1.75、次级字色（--nc-text-muted），与标题产生明快舒适的阅读反差 */
.nr-desc {
  font-size: 13.8px;
  line-height: 1.75;
  color: var(--nc-text-muted);
  white-space: pre-wrap;
  word-break: break-word;
}

@media (max-width: 768px) {
  .nr-scroll-body {
    padding: 12px;
  }
  .nr-row {
    flex-direction: column;
    align-items: stretch;
  }
  .nr-ctrl,
  .nr-mode-ctrl,
  .nr-field-limit,
  .nr-limit-ctrl {
    width: 100%;
    min-width: unset;
  }
  .nr-actions {
    margin-top: 4px;
  }
  .nr-btn-query {
    width: 100%;
  }
  .nr-item-content {
    padding: 14px 16px;
  }
  .nr-tags {
    display: none;
  }
  .nr-author {
    max-width: 90px;
  }
}
</style>
