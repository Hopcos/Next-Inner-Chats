export interface NewsItem {
  id: string
  guid: string
  source: string
  categories: string[]
  published_at: string
  author: string
  link: string
  language: string // 'zh' or 'other'
  translated: boolean
  title_zh: string
  description_zh: string
  title_original: string
  description_original: string
  score?: number | null
}

export interface NewsListResponse {
  count: number
  items: NewsItem[]
}

export interface NewsSearchResponse {
  count: number
  items: NewsItem[]
  query: string
  mode: string
}

export interface NewsSource {
  name: string
  url: string
  urls: string[]
  categories: string[]
  enabled: boolean
}

export interface SourcesResponse {
  sources: NewsSource[]
}

export type SearchMode = 'hybrid' | 'dense' | 'sparse'
