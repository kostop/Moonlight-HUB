# 仓库维护约定

- 只在 `kostop/Moonlight-HUB` 维护、提交和推送用户功能更新。
- Deskflow 作为 Moonlight Hub 的随附组件构建和使用，不单独发布或更新 Deskflow 仓库。
- Deskflow 核心改动保存在本仓库的 `deskflow-patches/`，触摸适配源码保存在 `native/deskflow-touch/`。
- `artifacts/` 是可重新生成的构建目录，不提交其中的上游检出、配置、凭据或二进制。
- 嵌入式代码尽量简洁易懂、高效，减少结构体和函数嵌套；函数参数添加中文注释。大量关联参数或变量使用结构体组织。
