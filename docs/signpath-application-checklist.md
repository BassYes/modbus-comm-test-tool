# SignPath.io OSS 申请与接入清单

## 申请前
- [ ] 仓库公开
- [ ] 添加 OSI 许可证（当前：MIT）
- [ ] README 包含隐私声明
- [ ] README 包含 SignPath 声明
- [ ] CI 可复现构建
- [ ] 团队是源码与构建脚本维护者

## SignPath 配置
- [ ] 创建 Organization / Project
- [ ] 获取 Organization ID
- [ ] 获取 Project slug
- [ ] 获取 Signing policy slug
- [ ] 创建 API Token（submitter 权限）
- [ ] 配置团队角色

## GitHub Secrets
- [ ] SIGNPATH_API_TOKEN
- [ ] SIGNPATH_ORG_ID
- [ ] UPDATE_SIGNING_KEY_BASE64（更新清单签名）

## CI 接入
- [ ] GitHub-hosted runner
- [ ] upload unsigned artifact
- [ ] submit signing request
- [ ] wait for completion
- [ ] download signed artifact
- [ ] generate update manifest
- [ ] sign update manifest
- [ ] verify update manifest
- [ ] upload release assets

## 发布后
- [ ] 验证 SmartScreen 行为
- [ ] 验证更新检查
- [ ] 记录发布结果
