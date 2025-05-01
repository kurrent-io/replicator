<template>
  <div id="app">
    <el-container style="border: 1px solid #eee">
      <el-header>
        <el-menu default-active="1" class="el-menu-demo" mode="horizontal">
          <el-menu-item index="1">
            <router-link to="/">Home</router-link>
          </el-menu-item>
          <el-menu-item index="2" disabled>
            <router-link to="/about">About</router-link>
          </el-menu-item>
        </el-menu>
      </el-header>
      <el-main>
        <router-view/>
      </el-main>
    </el-container>
  </div>
</template>

