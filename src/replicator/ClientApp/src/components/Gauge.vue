<template>
  <el-card shadow="never">
    <span class="percentage-value">{{ value }}</span>
    <span class="percentage-label">{{ title }}</span>
  </el-card>
</template>

<script>
export default {
    name: "Gauge",
    props: {
        title: String,
        value: String
    }
}
</script>

<style scoped lang="stylus">
.percentage-value
  display block
  text-align center
  margin-top 10px
  font-size 28px

.percentage-label
  display block
  text-align center
  margin-top 10px
  font-size 10px
</style>
